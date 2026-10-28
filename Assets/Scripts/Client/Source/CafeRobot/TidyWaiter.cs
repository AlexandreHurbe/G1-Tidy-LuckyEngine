using System;
using System.Collections.Generic;
using Hazel;
using Hazel.Data;

namespace Cafe.Robotics
{
	/// <summary>
	/// The combined task. Attach to the cafe G1's root entity (RobotController + MujocoScene), in place of
	/// <see cref="CafeWaiter"/>. Each episode:
	///
	///   1. resets the simulation (robot upright at its start, every body back where it was authored),
	///   2. shuffles the room's movable furniture and scatters the items along the counter's front edge,
	///   3. checks that every item can be picked up and every delivery spot reached from this layout
	///      (reshuffling if not),
	///   4. delivers the items one by one to spots along the dining table's edge, using the cafe's own
	///      navigation, pick-up, carry and set-down (<see cref="DeliverCup"/>), and
	///   5. scores the episode and starts the next.
	///
	/// Recording: one Observer episode per tidy-up, with a success flag (all items delivered) and a task label.
	/// Everything is reproducible from <see cref="Seed"/> + the episode number.
	///
	/// This file has not been compiled or run yet.
	/// </summary>
	public class TidyWaiter : Entity
	{
		// ── Scene ─────────────────────────────────────────────────────────
		[Group("Scene")]
		[Tooltip("Static box the items are picked up from (a counter about 1.0 m high). Its front edge is used.")]
		public Entity? Counter;

		[Group("Scene")]
		[Tooltip("Static table the items are delivered to (a table top about 0.75 m high; the cafe's communal table, with its free joint removed). Its long edge facing the room is used.")]
		public Entity? Table;

		[Group("Scene")]
		[Tooltip("Items to deliver, comma separated entity names. Each needs a MuJoCo collider and a free joint, and its origin at its base.")]
		public string ItemNames = "Item_1,Item_2,Item_3";

		[Group("Scene")]
		[Tooltip("Furniture to shuffle each episode, comma separated entity names (each with a MuJoCo collider and a free joint). Do not list the counter or the table.")]
		public string FurnitureNames = "Chair_01,Chair_02,Chair_03,Chair_04,BarStool_01,BarStool_02,TableSquare_01,TableBistro_01,FiddleFig_01,SnakePlant_01";

		[Group("Scene")]
		[Tooltip("The room's long single-box walls, comma separated entity names. The planner's reach check skips them (see ApproachPlanner.CorridorIgnoredBodies).")]
		public string WallNames = "Wall_North,Wall_South,Wall_East,Wall_West";

		// ── Episodes ──────────────────────────────────────────────────────
		[Group("Episodes")]
		[Tooltip("Episodes to run, then stop. 0 = run forever.")]
		public int MaxEpisodes = 0;

		[Group("Episodes")]
		[Tooltip("Base random seed. Episode N uses Seed + N. 0 = pick one from the clock at Play start.")]
		public int Seed = 0;

		[Group("Episodes")] [Units("s")]
		[Tooltip("Stand still this long after a reset and reshuffle so everything settles.")]
		public float SettleTime = 1.5f;

		[Group("Episodes")] [Units("s")]
		public float PauseBetweenEpisodes = 1.5f;

		[Group("Episodes")] [Units("s")]
		[Tooltip("Give up on an episode after this long.")]
		public float EpisodeTimeout = 420f;

		[Group("Episodes")]
		[Tooltip("How many layouts to try before giving an episode up as impossible.")]
		public int MaxLayoutRetries = 10;

		// ── Layout ────────────────────────────────────────────────────────
		[Group("Layout")]
		[Tooltip("Furniture goes inside this box (only X and Z are used). Keep it inside the walls.")]
		public Vector3 RoomMin = new Vector3(-3.9f, 0f, -2.9f);

		[Group("Layout")]
		public Vector3 RoomMax = new Vector3(3.9f, 0f, 2.9f);

		[Group("Layout")] [Units("m")]
		[Tooltip("Nothing is placed within this distance of the robot's start.")]
		public float StartKeepOut = 0.9f;

		[Group("Layout")] [Units("m")]
		[Tooltip("Smallest gap between pieces the robot has to pass between.")]
		public float MinGap = 0.75f;

		[Group("Layout")] [Units("m")]
		[Tooltip("Furniture stays this far out from the counter's front and from each delivery spot, so the robot has room to stand.")]
		public float WorkAreaRadius = 0.55f;

		// ── Items and spots ───────────────────────────────────────────────
		[Group("Items")] [Units("m")]
		[Tooltip("Item centre this far in from the counter's front edge (min). The grasp reaches at most about 11 cm in.")]
		public float ItemInsetMin = 0.045f;

		[Group("Items")] [Units("m")]
		public float ItemInsetMax = 0.075f;

		[Group("Items")] [Units("m")]
		[Tooltip("Keep items this far from the ends of the counter edge.")]
		public float ItemMargin = 0.35f;

		[Group("Items")] [Units("m")]
		[Tooltip("Smallest distance between two items along the counter.")]
		public float ItemSpacing = 0.32f;

		[Group("Items")] [Units("rad")]
		[Tooltip("Items are turned by a random angle within +- this about the vertical.")]
		public float ItemYawJitter = 0.2f;

		[Group("Spots")] [Units("m")]
		[Tooltip("Delivery spot this far in from the table's edge (the base centre of the item).")]
		public float SpotInset = 0.07f;

		[Group("Spots")] [Units("m")]
		[Tooltip("Keep spots this far from the ends of the table edge. Spots are spread evenly along it.")]
		public float SpotMargin = 0.30f;

		[Group("Layout")] [Units("m")]
		[Tooltip("Extra clearance the layout check demands of every drive between stances, beyond the planner's own minimum.")]
		public float RouteMargin = 0.03f;

		[Group("Layout")] [Units("m")]
		[Tooltip("How far the dining table keeps from the walls when it is placed (the counter always stands against one).")]
		public float TableWallMargin = 0.8f;

		// ── Carry (the cafe's defaults) ───────────────────────────────────
		[Group("Carry")]
		[Tooltip("Finger grip (0 open .. 1 fist) while carrying an item.")]
		public float CarryGrip = 0.35f;

		[Group("Carry")]
		public float CarryThumbWrap = 0.12f;

		[Group("Carry")]
		[Tooltip("Palm-site position while carrying, in the torso frame: forward, toward the hand side, height (m).")]
		public Vector3 CarryPosition = new Vector3(0.163f, 0.01f, 0.86f);

		[Group("Carry")]
		[Tooltip("Grip the fingers loosen to when the hand lands on the table with the item still above it, so it can slide down.")]
		public float LooseGrip = 0.15f;

		[Group("Carry")]
		[Tooltip("Seconds the fingers take to loosen to LooseGrip when the hand lands first.")]
		public float SlideTime = 0.5f;

		[Group("Carry")]
		[Tooltip("Grip the fingers relax to before opening, after the item is down.")]
		public float ReleaseGrip = 0.12f;

		[Group("Carry")]
		[Tooltip("How far below the item's centre the palm grips it (m). Lower = the item's base hangs further below the hand when it lands.")]
		public float GripBelowCenter = 0.005f;

		// ── Navigation ────────────────────────────────────────────────────
		[Group("Navigation")] [Units("m")]
		[Tooltip("Cell size of the floor map the robot plans on.")]
		public float NavCellSize = 0.05f;

		// ── Recording ─────────────────────────────────────────────────────
		[Group("Recording")]
		[Tooltip("Record each tidy-up with the Observer: one episode per tidy-up, success = every item delivered. "
			+ "Needs a LuckyHub sign-in, the Observer enabled in Scene Settings, and plain (not path-traced) cameras bound in the Data Recorder.")]
		public bool Record = true;

		[Group("Recording")]
		public string TaskLabel = "pick up the cups from the counter and place them side by side on the dining table";

		[Group("Debug")] [Units("s")]
		[Tooltip("Log the robot's state this often (0 = off).")]
		public float StatusLogInterval = 10f;

		private enum Stage { Boot, Settle, Delivering, Pausing, Stopped }

		private const float FallTilt = 0.7f;           // rad
		private const float FallPelvisHeight = 0.45f;  // m

		private G1Robot? m_Robot;
		private Navigator? m_Nav;
		private CupHandling? m_CupHandling;
		private readonly MotionTuning m_Motion = new MotionTuning();
		private readonly GraspTuning m_Grasp = new GraspTuning();
		private G1RoomNav.LayoutRandomizer? m_Layout;

		private readonly List<SceneObject> m_Items = new List<SceneObject>();
		private readonly List<uint> m_ItemQpos = new List<uint>();
		private readonly List<Vector3> m_Spots = new List<Vector3>();
		private G1RoomNav.EdgeLayout.Edge m_CounterEdge;
		private G1RoomNav.EdgeLayout.Edge m_TableEdge;
		private Vector3 m_Start;
		private Vector3 m_RoomCenter;
		private bool m_MovesMain;

		private Stage m_Stage = Stage.Boot;
		private Behavior? m_Active;
		private System.Random m_Rng = new System.Random(0);
		private int m_BaseSeed;
		private int m_Episode;
		private int m_LayoutTry;
		private int m_ItemIndex;
		private float m_EpisodeTime;
		private float m_FallTimer;
		private float m_StatusTimer;
		private int m_EpisodeDelivered;
		private readonly List<string> m_ItemLog = new List<string>();

		private int m_Succeeded, m_Failed, m_TotalDelivered, m_TotalItems, m_Falls;
		private readonly Dictionary<string, int> m_Failures = new Dictionary<string, int>();

		private bool m_RecordingOff;
		private bool m_EverRecorded;
		private int m_Recorded;

		public int Episode => m_Episode;

		// ── Frame loop ────────────────────────────────────────────────────

		protected override void OnUpdate(float ts)
		{
			if (m_Stage == Stage.Stopped)
				return;
			if (m_Robot == null && !TryInitialize())
				return;
			G1Robot robot = m_Robot!;
			float dt = Math.Clamp(ts, 0f, 0.1f);

			robot.Sense(dt);
			if (m_Stage == Stage.Delivering)
			{
				m_EpisodeTime += dt;
				if (HasFallen(dt))
				{
					m_Falls++;
					Log.Error($"[Tidy] Fall detected (tilt {robot.Tilt * Mathf.Rad2Deg:F0} deg, pelvis {robot.PelvisPosition.Y:F2} m); ending the episode");
					m_Active?.Abort("fell");
					CountFailure("fell");
					EndEpisode("fell");
				}
				else if (m_EpisodeTime > EpisodeTimeout)
				{
					m_Active?.Abort("timed out");
					CountFailure("timed out");
					EndEpisode("timed out");
				}
				else
					Step(dt);
			}
			else
				Step(dt);
			robot.Act(dt);

			if (StatusLogInterval > 0f && m_Stage == Stage.Delivering && (m_StatusTimer += dt) >= StatusLogInterval)
			{
				m_StatusTimer = 0f;
				Log.Info($"[Tidy] episode {m_Episode} t={m_EpisodeTime:F0}s item {m_ItemIndex + 1}/{m_Items.Count}: {Describe(m_Active)} | "
					+ $"pelvis ({robot.PelvisPosition.X:F1}, {robot.PelvisPosition.Z:F1}) tilt {robot.Tilt * Mathf.Rad2Deg:F0}"
					+ (m_CupHandling != null && m_CupHandling.HeldStatus.Length > 0 ? " | " + m_CupHandling.HeldStatus : ""));
			}
		}

		protected override void OnDestroy()
		{
			m_Active?.Abort("script destroyed");
			m_Robot?.Locomotion.Shutdown();
		}

		// ── Set-up ────────────────────────────────────────────────────────

		private bool TryInitialize()
		{
			G1Robot? robot = G1Robot.Create(this);
			if (robot == null)
			{
				Log.Error("[Tidy] Robot setup is incomplete; the task is disabled");
				m_Stage = Stage.Stopped;
				return false;
			}
			if (Counter == null || Table == null)
			{
				Log.Error("[Tidy] Assign the Counter and the Table in the TidyWaiter inspector");
				m_Stage = Stage.Stopped;
				return false;
			}

			foreach (string raw in ItemNames.Split(','))
			{
				string name = raw.Trim();
				if (name.Length == 0)
					continue;
				Entity? e = FindEntityByTag(name);
				if (e == null)
				{
					Log.Warn($"[Tidy] item '{name}' not found in the scene");
					continue;
				}
				var item = new SceneObject(robot, e);
				uint adr = G1RoomNav.FreeBodyPlacer.FindQposAdr(robot.Mujoco, e);
				if (!item.IsValid || !item.CanReset || adr == uint.MaxValue)
				{
					Log.Warn($"[Tidy] item '{name}' needs a MuJoCo collider and a free joint; skipped");
					continue;
				}
				m_Items.Add(item);
				m_ItemQpos.Add(adr);
			}
			if (m_Items.Count == 0)
			{
				Log.Error("[Tidy] No usable items; the task is disabled");
				m_Stage = Stage.Stopped;
				return false;
			}

			var furniture = new List<Entity>();
			foreach (string raw in FurnitureNames.Split(','))
			{
				string name = raw.Trim();
				if (name.Length == 0)
					continue;
				Entity? e = FindEntityByTag(name);
				if (e == null)
					Log.Warn($"[Tidy] furniture '{name}' not found in the scene");
				else
					furniture.Add(e);
			}

			// The cafe's reach-corridor check treats each collider as a bounding sphere, so a long single-box wall always
			// looks "in the way of the hand". Tell it to skip the walls (the floor map still treats them as obstacles).
			ApproachPlanner.CorridorIgnoredBodies.Clear();
			foreach (string raw in WallNames.Split(','))
			{
				string name = raw.Trim();
				if (name.Length == 0)
					continue;
				Entity? wall = FindEntityByTag(name);
				uint wallBody = wall == null ? uint.MaxValue : robot.Mujoco.GetBodyID("lucky_body_" + wall.ID);
				if (wallBody == uint.MaxValue)
					Log.Warn($"[Tidy] wall '{name}' not found or has no collider body");
				else
					ApproachPlanner.CorridorIgnoredBodies.Add((int)wallBody);
			}

			m_Robot = robot;
			m_Grasp.CarryGrip = CarryGrip;
			m_Grasp.ThumbWrap = CarryThumbWrap;
			m_Grasp.LooseGrip = LooseGrip;
			m_Grasp.SlideTime = SlideTime;
			m_Grasp.ReleaseGrip = ReleaseGrip;
			m_Grasp.CupInPalm = new Vector3(m_Grasp.CupInPalm.X, m_Grasp.CupInPalm.Y, GripBelowCenter);
			m_Grasp.CarryForward = CarryPosition.X;
			m_Grasp.CarryLateral = CarryPosition.Y;
			m_Grasp.CarryHeight = CarryPosition.Z;
			var grid = new NavGrid(new Vector3(RoomMin.X, 0f, RoomMin.Z), new Vector3(RoomMax.X, 0f, RoomMax.Z), NavCellSize);
			m_Nav = new Navigator(robot, grid, m_Motion);
			m_CupHandling = new CupHandling(robot, m_Grasp);
			robot.Initialize();

			m_Start = Planar.Flatten(Transform.WorldTranslation);
			m_RoomCenter = new Vector3(0.5f * (RoomMin.X + RoomMax.X), 0f, 0.5f * (RoomMin.Z + RoomMax.Z));
			// Edges come from the real collider shapes (the cafe table is a scale-1 mesh whose transform says nothing about its size).
			m_Layout = new G1RoomNav.LayoutRandomizer(robot.Mujoco, new G1RoomNav.WorldFrame(robot.Mujoco), furniture, Table.Tag) { MinGap = MinGap };
			// With a free joint the counter and the table are moved every episode; without one they stay where they were authored.
			bool counterMoves = m_Layout.AddMain(Counter, false);
			bool tableMoves = m_Layout.AddMain(Table, true);
			m_MovesMain = counterMoves || tableMoves;
			if (counterMoves != tableMoves)
				Log.Warn($"[Tidy] only the {(counterMoves ? "counter" : "table")} has a free joint, so only it is moved");
			UpdateEdges();
			float needed = 2f * SpotMargin + 0.30f * Math.Max(0, m_Items.Count - 1);
			if (m_TableEdge.Length < needed)
				Log.Warn($"[Tidy] the table's usable edge is {m_TableEdge.Length:F2} m but {m_Items.Count} spots need about {needed:F2} m; "
					+ "spots will be packed close and the set-down may fail. Lower SpotMargin or use fewer items.");
			// A round table has no straight edge: spots measured against its bounding rectangle can hang over the side.
			// CheckLayout rejects any spot with no table top under it, so a bad edge shows up as 'no workable layout'.

			m_BaseSeed = Seed != 0 ? Seed : Environment.TickCount & 0x7fffffff;
			m_RecordingOff = !Record;
			if (Record)
			{
				Observer.RegisterTask(0, TaskLabel);
				Observer.SetCurrentTaskIndex(0);
			}

			Log.Info($"[Tidy] Ready: {m_Items.Count} items, {m_Layout.Count} movable furniture pieces, base seed {m_BaseSeed}, "
				+ $"{(MaxEpisodes > 0 ? MaxEpisodes.ToString() : "unlimited")} episodes; counter edge {m_CounterEdge.Length:F1} m at height {m_CounterEdge.Top:F2}, "
				+ $"table edge {m_TableEdge.Length:F1} m at height {m_TableEdge.Top:F2}, nav grid {grid.Width}x{grid.Height}");
			BeginEpisode(reset: false);
			return true;
		}

		// ── Episodes ──────────────────────────────────────────────────────

		private void BeginEpisode(bool reset)
		{
			m_Episode++;
			m_LayoutTry = 0;
			m_ItemLog.Clear();
			m_EpisodeDelivered = 0;
			m_EpisodeTime = 0f;
			m_FallTimer = 0f;
			m_StatusTimer = 0f;
			m_Rng = new System.Random(unchecked(m_BaseSeed + m_Episode));
			m_Nav!.IgnoredBodies.Clear();
			m_CupHandling!.ClearHeld();
			if (reset)
				m_Robot!.ResetSimulation();   // robot upright at its start; every body back where it was authored

			if (!Reshuffle())
			{
				EndEpisode("no valid furniture layout");
				return;
			}
			m_Stage = Stage.Settle;
			m_Active = new Wait(m_Robot!, SettleTime, "Settle");
		}

		/// <summary>Choose and apply a new layout for the furniture and the items. False if none fits.</summary>
		private bool Reshuffle()
		{
			var startKeep = new List<(float X, float Z, float Radius)> { (m_Start.X, m_Start.Z, StartKeepOut) };
			// Several tries: the counter and table are placed first, and the furniture has to fit around them.
			for (int attempt = 0; attempt < 20; attempt++)
			{
				if (!m_Layout!.TryPlaceMain(m_Rng, RoomMin.X, RoomMax.X, RoomMin.Z, RoomMax.Z, startKeep, TableWallMargin))
					continue;
				UpdateEdges();   // where the counter's front and the table's spots are THIS episode

				var keepOut = new List<(float X, float Z, float Radius)>(startKeep);
				// Room to stand at the counter's front and beside each delivery spot.
				foreach (float along in new[] { -0.6f, 0f, 0.6f })
				{
					Vector3 p = m_CounterEdge.Outside(along, WorkAreaRadius);
					keepOut.Add((p.X, p.Z, WorkAreaRadius));
				}
				foreach (Vector3 spot in m_Spots)
				{
					Vector3 p = spot + m_TableEdge.Normal * (SpotInset + WorkAreaRadius);
					keepOut.Add((p.X, p.Z, WorkAreaRadius));
				}
				if (!m_Layout.TryGenerate(m_Rng, RoomMin.X, RoomMax.X, RoomMin.Z, RoomMax.Z, keepOut))
					continue;

				// Items: scattered along the counter's front edge, each turned a little.
				List<Vector3> points = G1RoomNav.EdgeLayout.RandomPoints(m_CounterEdge, m_Items.Count, ItemInsetMin, ItemInsetMax, ItemMargin, ItemSpacing, m_Rng);
				if (points.Count < m_Items.Count)
					continue;
				m_Layout.Apply();
				for (int i = 0; i < m_Items.Count; i++)
				{
					float yaw = (float)(m_Rng.NextDouble() * 2.0 - 1.0) * ItemYawJitter;
					Vector3 p = new Vector3(points[i].X, m_CounterEdge.Top + 0.002f, points[i].Z);
					G1RoomNav.FreeBodyPlacer.SetPose(m_Robot!.Mujoco, m_ItemQpos[i], p, yaw);
				}
				return true;
			}
			return false;
		}

		/// <summary>The counter's front edge and the table's delivery spots, from where the layout currently has them.</summary>
		private void UpdateEdges()
		{
			m_CounterEdge = G1RoomNav.EdgeLayout.FacingRoom(m_Layout!.CounterFootprint, m_RoomCenter, 1.0f);
			m_TableEdge = G1RoomNav.EdgeLayout.FacingRoom(m_Layout.TableFootprint, m_RoomCenter, 1.0f);
			m_Spots.Clear();
			m_Spots.AddRange(G1RoomNav.EdgeLayout.Spots(m_TableEdge, m_Items.Count, SpotInset, SpotMargin));
		}

		private void Step(float dt)
		{
			if (m_Active == null)
				return;
			Status status = m_Active.Tick(dt);
			if (status == Status.Running)
				return;

			switch (m_Stage)
			{
				case Stage.Settle:
					OnSettled();
					break;

				case Stage.Delivering:
					OnItemFinished(status);
					break;

				case Stage.Pausing:
					if (MaxEpisodes > 0 && m_Episode >= MaxEpisodes)
					{
						m_Stage = Stage.Stopped;
						m_Active = null;
						if (Observer.IsRecording)
							Observer.StopRecording();
						Log.Info($"[Tidy] done: {m_Succeeded}/{m_Episode} episodes fully delivered, {m_TotalDelivered}/{m_TotalItems} items, "
							+ $"{m_Falls} falls, {m_Recorded} recorded | failures: {FailureSummary()}");
					}
					else
						BeginEpisode(reset: true);
					break;
			}
		}

		/// <summary>Everything has settled: check the layout works, then start delivering.</summary>
		private void OnSettled()
		{
			string? problem = CheckLayout();
			if (problem != null)
			{
				if (++m_LayoutTry > MaxLayoutRetries)
				{
					Log.Warn($"[Tidy] episode {m_Episode}: giving up after {MaxLayoutRetries} layouts ({problem})");
					EndEpisode("no workable layout");
					return;
				}
				Log.Info($"[Tidy] episode {m_Episode}: layout rejected ({problem}); reshuffling (try {m_LayoutTry})");
				if (!Reshuffle())
				{
					EndEpisode("no valid furniture layout");
					return;
				}
				m_Active = new Wait(m_Robot!, SettleTime, "Settle");
				return;
			}

			Log.Info($"[Tidy] episode {m_Episode}: {m_Layout!.Describe()}");
			StartRecordingEpisode();
			m_Stage = Stage.Delivering;
			m_ItemIndex = 0;
			StartItem();
		}

		/// <summary>Every item must be pickable from here, and every spot reachable with the hand that would be holding.</summary>
		private string? CheckLayout()
		{
			// Stances first (the pick-up hand chosen here is the one that will set the item down), then the drives between
			// them, planned the way the robot will plan them: start -> pick-up 1 -> set-down 1 -> pick-up 2 -> ...
			var picks = new Stance[m_Items.Count];
			var places = new Stance[m_Items.Count];
			for (int i = 0; i < m_Items.Count; i++)
			{
				if (!ApproachPlanner.ForCup(m_Nav!, m_Grasp, m_Items[i], out picks[i], out string failure))
					return $"{m_Items[i]} cannot be picked up: {failure}";
				if (!ApproachPlanner.ForPlacement(m_Nav!, m_Grasp, m_Spots[i], m_Items[i].Body, picks[i].Hand, out places[i], out failure))
					return $"spot {i + 1} cannot be reached with the {picks[i].Hand.ToString().ToLowerInvariant()} hand: {failure}";
			}

			float planned = m_Nav!.Planner.MinClearance;
			m_Nav.Planner.MinClearance = planned + RouteMargin;   // a route that only just fits is not good enough: the walker wanders
			try
			{
				FloorPose from = m_Nav.RobotPose;
				for (int i = 0; i < m_Items.Count; i++)
				{
					m_Nav.IgnoredBodies.Clear();
					if (!Route(from, picks[i].Pose, out string failure))
						return $"no route to the pick-up stance for {m_Items[i]}: {failure}";
					m_Nav.IgnoredBodies.Add(m_Items[i].Body);   // carried: not an obstacle
					if (!Route(picks[i].Pose, places[i].Pose, out failure))
						return $"no route carrying {m_Items[i]} to its spot: {failure}";
					from = places[i].Pose;
				}
			}
			finally
			{
				m_Nav.Planner.MinClearance = planned;
				m_Nav.IgnoredBodies.Clear();
			}
			return null;
		}

		private bool Route(FloorPose from, FloorPose to, out string failure)
		{
			m_Nav!.RefreshMap();
			return m_Nav.Planner.TryPlan(m_Nav.Grid, from, to, out _, out failure);
		}

		private void StartItem()
		{
			SceneObject item = m_Items[m_ItemIndex];
			Vector3 spot = m_Spots[m_ItemIndex];
			m_CupHandling!.ClearHeld();
			m_Nav!.IgnoredBodies.Clear();
			var deliver = new DeliverCup(m_Nav, m_CupHandling, m_Grasp, item, () => spot);
			m_Active = deliver;
			Log.Info($"[Tidy] episode {m_Episode}: item {m_ItemIndex + 1}/{m_Items.Count} ({item}) -> spot ({spot.X:F2}, {spot.Z:F2})");
		}

		private void OnItemFinished(Status status)
		{
			SceneObject item = m_Items[m_ItemIndex];
			Vector3 spot = m_Spots[m_ItemIndex];
			m_TotalItems++;
			if (status == Status.Succeeded)
			{
				m_EpisodeDelivered++;
				m_TotalDelivered++;
				float off = Planar.Distance(item.PointAbovePivot(0f), spot);
				m_ItemLog.Add($"{item}: delivered ({off * 100f:F1} cm from the spot)");
			}
			else
			{
				string reason = Categorize(m_Active!.FailureReason);
				CountFailure(reason);
				m_ItemLog.Add($"{item}: FAILED ({reason})");
				Log.Warn($"[Tidy] item {item} failed: {m_Active.FailureReason}");
			}

			m_ItemIndex++;
			if (m_ItemIndex < m_Items.Count)
				StartItem();
			else
				EndEpisode(m_EpisodeDelivered == m_Items.Count ? "all delivered" : "some items failed");
		}

		private void EndEpisode(string reason)
		{
			bool success = m_EpisodeDelivered == m_Items.Count && m_Items.Count > 0;
			if (success)
				m_Succeeded++;
			else
				m_Failed++;
			Log.Info($"[Tidy] episode {m_Episode} {(success ? "SUCCESS" : "FAILED")}: {reason}; {m_EpisodeDelivered}/{m_Items.Count} items in {m_EpisodeTime:F0} s | "
				+ $"{string.Join("; ", m_ItemLog)} | totals: {m_Succeeded} ok, {m_Failed} failed, {m_TotalDelivered}/{m_TotalItems} items");
			if (m_MovesMain)
			{
				float drift = m_Layout!.MaxMainDrift(out string moved);
				if (drift > 0.02f)
					Log.Warn($"[Tidy] episode {m_Episode}: the {moved} was pushed {drift * 100f:F0} cm from where it was placed");
			}
			EndRecordingEpisode(success);
			m_Stage = Stage.Pausing;
			m_Active = new Wait(m_Robot!, PauseBetweenEpisodes, "Pause");
		}

		// ── Recording (as in RoomNavigator) ───────────────────────────────

		private void StartRecordingEpisode()
		{
			if (m_RecordingOff)
				return;
			if (Observer.IsRecording)
			{
				if (!Observer.IsEpisodeOpen)
					Observer.BeginEpisode();
			}
			else if (m_EverRecorded)
			{
				Log.Warn("[Tidy] the recording session has ended; the remaining episodes are not recorded.");
				m_RecordingOff = true;
				return;
			}
			else
			{
				Observer.StartRecording();
			}

			if (!Observer.IsEpisodeOpen)
			{
				Log.Error("[Tidy] the recorder did not start. Check that you are signed in to LuckyHub, Enable MuJoCo Observer is on "
					+ "in Settings > Scene > Recorder, and plain cameras are bound and enabled. The episodes still run, unrecorded.");
				m_RecordingOff = true;
				return;
			}
			m_EverRecorded = true;
		}

		private void EndRecordingEpisode(bool success)
		{
			if (m_RecordingOff || !Observer.IsRecording || !Observer.IsEpisodeOpen)
				return;
			Observer.EndCurrentEpisode(success);
			m_Recorded++;
		}

		// ── Helpers ───────────────────────────────────────────────────────

		private bool HasFallen(float dt)
		{
			G1Robot robot = m_Robot!;
			bool down = robot.Tilt > FallTilt || robot.PelvisPosition.Y < FallPelvisHeight;
			m_FallTimer = down ? m_FallTimer + dt : 0f;
			return m_FallTimer > 0.3f;
		}

		private void CountFailure(string category)
		{
			m_Failures.TryGetValue(category, out int n);
			m_Failures[category] = n + 1;
		}

		/// <summary>Collapse a failure chain ("Place failed: PlaceOn: SetDown: ...") to its innermost cause.</summary>
		private static string Categorize(string reason)
		{
			int cut = reason.LastIndexOf(": ", StringComparison.Ordinal);
			string tail = cut >= 0 ? reason[(cut + 2)..] : reason;
			int paren = tail.IndexOf(" (", StringComparison.Ordinal);
			return paren > 0 ? tail[..paren] : tail;
		}

		private string FailureSummary()
		{
			if (m_Failures.Count == 0)
				return "none";
			var parts = new List<string>();
			foreach (KeyValuePair<string, int> f in m_Failures)
				parts.Add($"{f.Key} x{f.Value}");
			return string.Join(", ", parts);
		}

		private static string Describe(Behavior? behavior) => behavior switch
		{
			DeliverCup task => $"{task.Name} / {task.PhaseName}" + (task.ActiveStep is Sequence s && s.Current != null ? $" / {s.Current.Name}" : ""),
			Sequence s when s.Current != null => $"{s.Name} / {s.Current.Name}",
			null => "idle",
			_ => behavior.Name,
		};
	}
}
