using AssetManagementBase;
using Nursia.ActionRPG.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Myra;
using Myra.Graphics2D.UI;
using Nursia.Rendering;
using Nursia.SceneGraph;
using Nursia.SceneGraph.Landscape;
using System;
using System.Collections.Generic;
using System.IO;

namespace Nursia.ActionRPG
{
	public class ViewerGame : Game
	{
		private const float MouseSensitivity = 0.2f;
		private const float MovementSpeed = 0.1f;
		private const float TreeCollisionRadius = 2.0f;

		private readonly GraphicsDeviceManager _graphics;
		private SpriteBatch _spriteBatch;
		private InputService _inputService;
		private Character _controllerService;
		private Scene _scene;
		private SceneNode _cameraMount = new SceneNode();
		private Camera _mainCamera = new Camera();
		private readonly ForwardRenderer _renderer = new ForwardRenderer();
		private readonly FramesPerSecondCounter _fpsCounter = new FramesPerSecondCounter();
		private readonly List<Vector3> _treePositions = new List<Vector3>();
		private TerrainNode _terrain;
		private Desktop _desktop;
		private MainPanel _mainPanel;

		/// <summary>Singleton instance of the ViewerGame for global access.</summary>
		public static ViewerGame Instance { get; private set; }

		private NursiaModelNode ModelNode => _controllerService.ModelNode;

		/// <summary>Initializes game with graphics and input configuration.</summary>
		public ViewerGame()
		{
			Instance = this;

			_graphics = new GraphicsDeviceManager(this)
			{
				PreferredBackBufferWidth = 1200,
				PreferredBackBufferHeight = 800
			};

			IsMouseVisible = true;
			Window.AllowUserResizing = true;

			if (Configuration.NoFixedStep)
			{
				IsFixedTimeStep = false;
				_graphics.SynchronizeWithVerticalRetrace = false;
			}
		}

		/// <summary>Loads all game content and scene setup.</summary>
		protected override void LoadContent()
		{
			base.LoadContent();

			// Nursia
			Nrs.Game = this;

			var assetManager = AssetManager.CreateFileAssetManager(Path.Combine(AppContext.BaseDirectory, "Assets"));
			_scene = assetManager.LoadStoredScene("Scenes/Main.scene");
			_terrain = _scene.Root.QueryFirstByType<TerrainNode>();

			// Add trees using instanced rendering
			var rng = new Random();

			var oakScene = assetManager.LoadStoredScene("Scenes/tree_oak.scene");
			var oakModelNode = oakScene.Root.QueryFirstByType<NursiaModelNode>();
			var pineScene = assetManager.LoadStoredScene("Scenes/tree_pineDefaultA.scene");
			var pineModelNode = pineScene.Root.QueryFirstByType<NursiaModelNode>();

			var oakModel = oakModelNode.Model;
			var oakMaterials = oakModelNode.Materials;
			var pineModel = pineModelNode.Model;
			var pineMaterials = pineModelNode.Materials;

			const float treeDensity = 0.003f;
			const float worldHalf = 95f;
			const float minSpacing = 5f;
			const int maxPlaceAttempts = 20;

			var treeCount = (int)((worldHalf * 2) * (worldHalf * 2) * treeDensity);
			_treePositions.Capacity = treeCount;
			var oakTransforms = new List<Matrix>();
			var pineTransforms = new List<Matrix>();

			for (int i = 0; i < treeCount; i++)
			{
				Vector3 position;
				float height;
				var attempts = 0;

				do
				{
					position = new Vector3(
						(float)(rng.NextDouble() * worldHalf * 2 - worldHalf),
						0f,
						(float)(rng.NextDouble() * worldHalf * 2 - worldHalf));
					height = _terrain.GetHeight(position);
					attempts++;
				}
				while ((_treePositions.Exists(p => Vector3.DistanceSquared(p, position) < minSpacing * minSpacing)
						|| height < 5.5f)
					   && attempts < maxPlaceAttempts);

				var yaw = MathHelper.ToRadians((float)(rng.NextDouble() * 360f));
				var transform = Matrix.CreateScale(8f) * Matrix.CreateRotationY(yaw)
					* Matrix.CreateTranslation(position.X, height, position.Z);

				(rng.Next(2) == 0 ? oakTransforms : pineTransforms).Add(transform);
				_treePositions.Add(position);
			}

			for (var mi = 0; mi < oakModel.Meshes.Length; mi++)
			{
				var mesh = oakModel.Meshes[mi];
				for (var pi = 0; pi < mesh.MeshParts.Count; pi++)
				{
					var instanced = new InstancedMeshNode
					{
						Mesh = mesh.MeshParts[pi],
						Material = oakMaterials[mi][pi]
					};
					foreach (var t in oakTransforms)
						instanced.InstancesTransforms.Add(t);
					_scene.Root.Children.Add(instanced);
				}
			}

			for (var mi = 0; mi < pineModel.Meshes.Length; mi++)
			{
				var mesh = pineModel.Meshes[mi];
				for (var pi = 0; pi < mesh.MeshParts.Count; pi++)
				{
					var instanced = new InstancedMeshNode
					{
						Mesh = mesh.MeshParts[pi],
						Material = pineMaterials[mi][pi]
					};
					foreach (var t in pineTransforms)
						instanced.InstancesTransforms.Add(t);
					_scene.Root.Children.Add(instanced);
				}
			}

			// Add character
			var characterModel = _scene.Root.QueryFirstByType<NursiaModelNode>();
			var swordScene = assetManager.LoadStoredScene("Scenes/Sword.scene");
			var swordModel = swordScene.Root.QueryFirstByType<NursiaModelNode>();
			_controllerService = new Character(characterModel, swordModel);

			var playerPos = ModelNode.Translation;
			playerPos.Y = _terrain.GetHeight(playerPos);
			ModelNode.Translation = playerPos;

			_cameraMount = _scene.Root.QueryFirstById("_cameraMount");
			_mainCamera = _scene.Root.QueryFirstByType<Camera>();

			// Input system with locked mouse for FPS control
			_inputService = new InputService();
			_inputService.MouseMoved += _inputService_MouseMoved;
			_inputService.KeyDown += _inputService_KeyDown;
			_inputService.MouseLocked = true;

			_spriteBatch = new SpriteBatch(GraphicsDevice);

			Nrs.GraphicsSettings.MaxShadowDistance = 100.0f;

			// UI panel
			MyraEnvironment.Game = this;
			_desktop = new Desktop();
			_mainPanel = new MainPanel();
			_desktop.Root = _mainPanel;
		}

		private Vector3 ResolveTreeCollision(Vector3 position, Vector3 velocity)
		{
			var radiusSq = TreeCollisionRadius * TreeCollisionRadius;

			Vector3 flat(Vector3 v) => new Vector3(v.X, 0f, v.Z);

			var newPos = position + velocity;

			if (_treePositions.TrueForAll(t => Vector3.DistanceSquared(flat(newPos), flat(t)) >= radiusSq))
				return velocity;

			var xOnly = new Vector3(velocity.X, 0f, 0f);
			if (_treePositions.TrueForAll(t => Vector3.DistanceSquared(flat(position + xOnly), flat(t)) >= radiusSq))
				return xOnly;

			var zOnly = new Vector3(0f, 0f, velocity.Z);
			if (_treePositions.TrueForAll(t => Vector3.DistanceSquared(flat(position + zOnly), flat(t)) >= radiusSq))
				return zOnly;

			return Vector3.Zero;
		}

		/// <summary>Handles keyboard events (Escape toggles mouse lock).</summary>
		private void _inputService_KeyDown(object sender, KeyEventsArgs e)
		{
			// Escape toggles between locked mouse (FPS camera control) and free mouse (UI interaction)
			if (e.Key == Keys.Escape)
			{
				_inputService.MouseLocked = !_inputService.MouseLocked;
			}
		}

		/// <summary>Rotates character/camera based on mouse movement (when locked).</summary>
		private void _inputService_MouseMoved(object sender, InputEventArgs<Point> e)
		{
			if (!_inputService.MouseLocked)
				return;

			var playerRotation = ModelNode.Rotation;
			playerRotation.Y += -(int)((e.NewValue.X - e.OldValue.X) * MouseSensitivity);
			ModelNode.Rotation = playerRotation;

			var cameraRotation = _cameraMount.Rotation;
			cameraRotation.X += (int)((e.NewValue.Y - e.OldValue.Y) * MouseSensitivity);
			_cameraMount.Rotation = cameraRotation;
		}

		/// <summary>Updates game logic: input, animations, FPS counter.</summary>
		protected override void Update(GameTime gameTime)
		{
			base.Update(gameTime);

			_inputService.Update();

			// Process WASD movement
			var isRunning = false;
			var velocity = Vector3.Zero;

			if (_inputService.IsKeyDown(Keys.W))
			{
				velocity = ModelNode.GlobalTransform.Forward * -MovementSpeed;
				isRunning = true;
			}
			else if (_inputService.IsKeyDown(Keys.S))
			{
				velocity = ModelNode.GlobalTransform.Forward * MovementSpeed;
				isRunning = true;
			}
			else if (_inputService.IsKeyDown(Keys.A))
			{
				velocity = ModelNode.GlobalTransform.Right * MovementSpeed;
				isRunning = true;
			}
			else if (_inputService.IsKeyDown(Keys.D))
			{
				velocity = ModelNode.GlobalTransform.Right * -MovementSpeed;
				isRunning = true;
			}

			if (_inputService.IsKeyDown(Keys.Space))
				_controllerService.Jump(velocity);

			if (_inputService.IsKeyDown(Keys.LeftShift))
				_controllerService.Slash();

			if (_inputService.IsKeyDown(Keys.R))
			{
				if (_controllerService.WeaponDrawn)
					_controllerService.SheathWeapon();
				else
					_controllerService.DrawWeapon();
			}

			if (isRunning)
			{
				velocity = ResolveTreeCollision(ModelNode.Translation, velocity);
				_controllerService.Run(velocity);
			}
			else
				_controllerService.Idle();

			_controllerService.GroundY = _terrain.GetHeight(ModelNode.Translation);
			_controllerService.Update(gameTime.ElapsedGameTime);

			if (!_controllerService.IsJumping)
			{
				var pos = ModelNode.Translation;
				pos.Y = _terrain.GetHeight(pos);
				ModelNode.Translation = pos;
			}

			_scene.Update(gameTime);
		}

		/// <summary>Renders 3D scene and UI overlay.</summary>
		protected override void Draw(GameTime gameTime)
		{
			base.Draw(gameTime);

			GraphicsDevice.Clear(Color.Black);

			// Render the scene
			_scene.Render(_renderer, _mainCamera);

			// Update UI statistics
			_mainPanel._labelFPS.Text = $"FPS: {_fpsCounter.FramesPerSecond}";
			var stats = _renderer.Statistics;
			_mainPanel._labelEffectsSwitches.Text = stats.EffectsSwitches.ToString();
			_mainPanel._labelDrawCalls.Text = stats.DrawCalls.ToString();
			_mainPanel._labelVerticesDrawn.Text = stats.VerticesDrawn.ToString();
			_mainPanel._labelPrimitivesDrawn.Text = stats.PrimitivesDrawn.ToString();
			_mainPanel._labelPassesDrawn.Text = stats.Passes.ToString();

			_desktop.Render();

			_fpsCounter.OnFrameDrawn();
		}
	}
}
