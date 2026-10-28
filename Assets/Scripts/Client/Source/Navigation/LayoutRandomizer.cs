using System;
using System.Collections.Generic;
using Hazel;

namespace G1RoomNav
{
	/// <summary>
	/// Moves the room's furniture to random, non-overlapping poses. Each piece must be a MuJoCo body with
	/// a free joint (a MuJoCo collider plus a MujocoJoint of type free): MuJoCo owns its pose, so it is
	/// moved by writing the free joint's qpos (position + quaternion), the same way RobotCafe puts its
	/// cup back. Walls and the ground stay static.
	///
	/// Chairs are placed around the table; everything else is sampled uniformly in the room and rejected
	/// when it overlaps another piece (with a gap wide enough for the G1 to pass) or the keep-out discs.
	/// </summary>
	public sealed class LayoutRandomizer
	{
		private const int FreeJointQposSize = 7;   // position + quaternion

		private sealed class Piece
		{
			public string Name = string.Empty;
			public uint QposAdr;
			public float HalfX, HalfZ, Height;   // footprint half-extents and full height (m)
			public bool IsChair, IsTable, IsCounter, Movable;
			public float X, Z, Yaw;              // chosen pose: footprint centre on the floor, heading about +Y
			// The footprint centre is not where the entity's transform is (cafe props have their pivot at
			// the floor under one end). OffU/OffV: centre relative to the pivot along the footprint axes.
			public float OffU, OffV, PivotY;
			public float TargetX, TargetZ;       // where the pivot was last sent (for the placement check)

			public static Piece From(Entity e, Footprint fp) => new Piece
			{
				Name = e.Tag,
				HalfX = fp.HalfX,
				HalfZ = fp.HalfZ,
				Height = fp.Top,
				OffU = fp.OffU,
				OffV = fp.OffV,
				PivotY = fp.PivotY,
				X = fp.CenterX,
				Z = fp.CenterZ,
				Yaw = fp.Yaw,
			};
		}

		/// <summary>Gap kept between pieces the robot has to pass between (m). The G1 is ~0.5 m wide.</summary>
		public float MinGap = 0.75f;
		/// <summary>Gap between a chair and the table it stands at (m).</summary>
		public float ChairTableGap = 0.03f;
		public int TriesPerPiece = 200;
		public int LayoutAttempts = 40;

		private readonly MujocoSceneComponent m_Mujoco;
		private readonly Vector3 m_Origin;
		private readonly Quaternion m_HzToMj;
		private readonly WorldFrame m_Frame;
		private readonly List<Piece> m_Pieces = new List<Piece>();
		// Pieces that never move (a counter, the dining table): other pieces keep clear of them, and chairs
		// are placed around the fixed table.
		private readonly List<Piece> m_Fixed = new List<Piece>();

		public int Count => m_Pieces.Count;

		/// <summary>
		/// Register a static prop (a box, or a cafe mesh with a compound collider) as an obstacle for the shuffle.
		/// Its footprint is measured from its collider shapes. <paramref name="isTable"/> marks the table the
		/// chairs gather around.
		/// </summary>
		public void AddFixed(Entity e, bool isTable)
		{
			Piece piece = Piece.From(e, PropFootprint.Measure(m_Mujoco, m_Frame, e));
			piece.IsTable = isTable;
			m_Fixed.Add(piece);
		}

		// The counter and the dining table: obstacles for the furniture shuffle like fixed pieces, but moved each episode
		// when they have a free joint (see TryPlaceMain). Without one they stay where they are.
		private readonly List<Piece> m_Main = new List<Piece>();

		/// <summary>
		/// Register the counter (<paramref name="isTable"/> false) or the dining table (true). Returns true if it has a free
		/// joint and will be moved each episode; false if it stays put (it is then just an obstacle, as with <see cref="AddFixed"/>).
		/// </summary>
		public bool AddMain(Entity e, bool isTable)
		{
			Piece piece = Piece.From(e, PropFootprint.Measure(m_Mujoco, m_Frame, e));
			piece.IsTable = isTable;
			piece.IsCounter = !isTable;
			piece.QposAdr = FreeBodyPlacer.FindQposAdr(m_Mujoco, e);
			piece.Movable = piece.QposAdr != uint.MaxValue;
			m_Fixed.Add(piece);
			if (piece.Movable)
			{
				m_Main.Add(piece);
				m_Main.Sort((a, b) => b.IsCounter.CompareTo(a.IsCounter));   // counter first, then the table
			}
			return piece.Movable;
		}

		public Footprint CounterFootprint => ToFootprint(m_Fixed.Find(p => p.IsCounter));
		public Footprint TableFootprint => ToFootprint(m_Fixed.Find(p => p.IsTable));

		private static Footprint ToFootprint(Piece? p) =>
			p == null ? default : new Footprint(p.X, p.Z, p.HalfX, p.HalfZ, p.Yaw, p.Height, 0f, p.PivotY, 0f, p.OffU, p.OffV, true);

		/// <summary>
		/// Choose new poses for the counter and the table: the counter flush against a random wall (front facing the room),
		/// the table anywhere clear at least <paramref name="tableWallMargin"/> from the walls. Both keep out of the discs in
		/// <paramref name="keepOut"/> (the robot's start) and clear of each other. Call before <see cref="TryGenerate"/>, which
		/// then places the rest around them. Nothing is written to the simulation until <see cref="Apply"/>.
		/// </summary>
		public bool TryPlaceMain(System.Random rng, float minX, float maxX, float minZ, float maxZ,
			IReadOnlyList<(float X, float Z, float Radius)> keepOut, float tableWallMargin)
		{
			var done = new List<Piece>();
			foreach (Piece f in m_Fixed)
				if (!f.Movable)
					done.Add(f);
			foreach (Piece p in m_Main)
			{
				bool ok = false;
				for (int t = 0; t < TriesPerPiece * 3 && !ok; t++)
				{
					if (p.IsCounter)
						ChooseWallPose(rng, p, minX, maxX, minZ, maxZ);
					else
						ChooseFreePose(rng, p, minX + tableWallMargin, maxX - tableWallMargin, minZ + tableWallMargin, maxZ - tableWallMargin);
					ok = InsideRoom(p, minX, maxX, minZ, maxZ) && ClearOfKeepOut(p, keepOut) && ClearOfPlaced(p, done);
				}
				if (!ok)
					return false;
				done.Add(p);
			}
			return true;
		}

		/// <summary>Flush against one of the four walls, anywhere along it except the corners; the long side faces the room.</summary>
		private static void ChooseWallPose(System.Random rng, Piece p, float minX, float maxX, float minZ, float maxZ)
		{
			const float corner = 0.5f;
			int wall = rng.Next(4);   // 0 north (min Z), 1 south (max Z), 2 east (max X), 3 west (min X)
			// The box is symmetric, so a half turn is the same shape: only the long side's direction matters.
			p.Yaw = (wall < 2 ? 0f : MathF.PI * 0.5f) + (rng.Next(2) == 0 ? 0f : MathF.PI);
			Extent(p, out float ex, out float ez);
			if (wall < 2)
			{
				p.Z = wall == 0 ? minZ + ez : maxZ - ez;
				p.X = maxX - ex - corner < minX + ex + corner ? float.NaN : Range(rng, minX + ex + corner, maxX - ex - corner);
			}
			else
			{
				p.X = wall == 2 ? maxX - ex : minX + ex;
				p.Z = maxZ - ez - corner < minZ + ez + corner ? float.NaN : Range(rng, minZ + ez + corner, maxZ - ez - corner);
			}
		}

		/// <summary>How far the counter or the table has been pushed from where it was placed (m); the name of the one that moved most.</summary>
		public float MaxMainDrift(out string worst)
		{
			float max = 0f;
			worst = string.Empty;
			foreach (Piece p in m_Main)
			{
				float x = m_Mujoco.GetJointPosition(p.QposAdr + 0), y = m_Mujoco.GetJointPosition(p.QposAdr + 1), z = m_Mujoco.GetJointPosition(p.QposAdr + 2);
				Vector3 world = m_Frame.Point(new Vector3(x, y, z));
				float err = MathF.Sqrt((world.X - p.TargetX) * (world.X - p.TargetX) + (world.Z - p.TargetZ) * (world.Z - p.TargetZ));
				if (err > max)
				{
					max = err;
					worst = p.IsCounter ? "counter" : "table";
				}
			}
			return max;
		}

		public LayoutRandomizer(MujocoSceneComponent mujoco, WorldFrame frame, IEnumerable<Entity> furniture, string tableTag)
		{
			m_Mujoco = mujoco;
			m_Frame = frame;
			m_Origin = mujoco.GetSceneTranslation();
			m_HzToMj = mujoco.GetAxisHzToMj();

			foreach (Entity e in furniture)
			{
				uint body = mujoco.GetBodyID("lucky_body_" + e.ID);
				uint joint = body == uint.MaxValue ? uint.MaxValue : mujoco.GetBodyFreeJointIndex(body);
				if (joint == uint.MaxValue)
				{
					Log.Warn($"[RoomNav] '{e.Tag}' has no free-jointed MuJoCo body; it will not be randomized.");
					continue;
				}
				Piece piece = Piece.From(e, PropFootprint.Measure(mujoco, frame, e));
				piece.QposAdr = mujoco.GetJointQposAdr(joint);
				piece.IsChair = e.Tag.StartsWith("Chair", StringComparison.Ordinal);
				piece.IsTable = e.Tag == tableTag;
				m_Pieces.Add(piece);
			}
			// Non-chairs first (table before chairs), so chairs can be placed around the table.
			m_Pieces.Sort((a, b) => a.IsChair.CompareTo(b.IsChair));
		}

		/// <summary>
		/// Choose new poses inside the box (minX..maxX, minZ..maxZ). <paramref name="keepOut"/> are discs
		/// (x, z, radius) that must stay clear, e.g. around the robot's start.
		/// </summary>
		public bool TryGenerate(System.Random rng, float minX, float maxX, float minZ, float maxZ,
			IReadOnlyList<(float X, float Z, float Radius)> keepOut)
		{
			for (int attempt = 0; attempt < LayoutAttempts; attempt++)
				if (TryLayout(rng, minX, maxX, minZ, maxZ, keepOut))
					return true;
			return false;
		}

		private bool TryLayout(System.Random rng, float minX, float maxX, float minZ, float maxZ,
			IReadOnlyList<(float X, float Z, float Radius)> keepOut)
		{
			var placed = new List<Piece>(m_Pieces.Count + m_Fixed.Count);
			Piece? table = null;
			foreach (Piece f in m_Fixed)
			{
				placed.Add(f);
				if (f.IsTable)
					table = f;
			}

			foreach (Piece p in m_Pieces)
			{
				bool ok = false;
				for (int t = 0; t < TriesPerPiece && !ok; t++)
				{
					if (p.IsChair && table != null)
						ChooseChairPose(rng, p, table);
					else
						ChooseFreePose(rng, p, minX, maxX, minZ, maxZ);

					ok = InsideRoom(p, minX, maxX, minZ, maxZ) && ClearOfKeepOut(p, keepOut) && ClearOfPlaced(p, placed);
				}
				if (!ok)
					return false;
				placed.Add(p);
				if (p.IsTable)
					table = p;
			}
			return true;
		}

		private static void ChooseFreePose(System.Random rng, Piece p, float minX, float maxX, float minZ, float maxZ)
		{
			// Mostly tidy (axis-aligned), sometimes at an angle.
			p.Yaw = rng.NextDouble() < 0.6 ? rng.Next(4) * MathF.PI * 0.5f : (float)(rng.NextDouble() * MathF.PI * 2.0);
			Extent(p, out float ex, out float ez);
			if (maxX - ex < minX + ex || maxZ - ez < minZ + ez)
			{
				p.X = float.NaN;
				return;
			}
			p.X = Range(rng, minX + ex, maxX - ex);
			p.Z = Range(rng, minZ + ez, maxZ - ez);
		}

		private void ChooseChairPose(System.Random rng, Piece chair, Piece table)
		{
			float angle = (float)(rng.NextDouble() * MathF.PI * 2.0);
			float dx = MathF.Cos(angle), dz = MathF.Sin(angle);
			// Distance from the table centre to its edge along (dx, dz), then the chair's own half-size.
			float cos = MathF.Cos(table.Yaw), sin = MathF.Sin(table.Yaw);
			float lx = dx * cos - dz * sin, lz = dx * sin + dz * cos;   // direction in the table's frame
			float tableReach = MathF.Min(
				MathF.Abs(lx) > 1e-4f ? table.HalfX / MathF.Abs(lx) : float.MaxValue,
				MathF.Abs(lz) > 1e-4f ? table.HalfZ / MathF.Abs(lz) : float.MaxValue);
			float d = tableReach + MathF.Max(chair.HalfX, chair.HalfZ) + ChairTableGap + (float)rng.NextDouble() * 0.25f;
			chair.X = table.X + dx * d;
			chair.Z = table.Z + dz * d;
			// Face the table. The cafe's chairs have their front along local +Z (the backrest extends toward -Z);
			// the box stools are symmetric, so the direction does not matter for them.
			chair.Yaw = MathF.Atan2(table.X - chair.X, table.Z - chair.Z);
		}

		private static void Extent(Piece p, out float ex, out float ez)
		{
			float c = MathF.Abs(MathF.Cos(p.Yaw)), s = MathF.Abs(MathF.Sin(p.Yaw));
			ex = c * p.HalfX + s * p.HalfZ;
			ez = s * p.HalfX + c * p.HalfZ;
		}

		private static bool InsideRoom(Piece p, float minX, float maxX, float minZ, float maxZ)
		{
			if (float.IsNaN(p.X))
				return false;
			Extent(p, out float ex, out float ez);
			return p.X - ex >= minX && p.X + ex <= maxX && p.Z - ez >= minZ && p.Z + ez <= maxZ;
		}

		private static bool ClearOfKeepOut(Piece p, IReadOnlyList<(float X, float Z, float Radius)> keepOut)
		{
			float c = MathF.Cos(p.Yaw), s = MathF.Sin(p.Yaw);
			for (int i = 0; i < keepOut.Count; i++)
			{
				float dx = keepOut[i].X - p.X, dz = keepOut[i].Z - p.Z;
				// Disc centre in the piece's frame (local X = (cos, -sin), local Z = (sin, cos)).
				float lx = dx * c - dz * s, lz = dx * s + dz * c;
				float cx = Math.Clamp(lx, -p.HalfX, p.HalfX), cz = Math.Clamp(lz, -p.HalfZ, p.HalfZ);
				float ex = lx - cx, ez = lz - cz;
				if (ex * ex + ez * ez < keepOut[i].Radius * keepOut[i].Radius)
					return false;
			}
			return true;
		}

		private bool ClearOfPlaced(Piece p, List<Piece> placed)
		{
			foreach (Piece q in placed)
				if (Overlaps(p, q, GapBetween(p, q)))
					return false;
			return true;
		}

		private float GapBetween(Piece a, Piece b)
		{
			if (a.IsChair && b.IsChair) return 0.10f;
			if (a.IsChair && b.IsTable || b.IsChair && a.IsTable) return ChairTableGap;
			if (a.IsChair || b.IsChair) return 0.5f * (MinGap + 0.25f);
			return MinGap;
		}

		/// <summary>Do two oriented footprints, each grown by half the gap, overlap? (separating axis test)</summary>
		private static bool Overlaps(Piece a, Piece b, float gap)
		{
			float ahx = a.HalfX + 0.5f * gap, ahz = a.HalfZ + 0.5f * gap;
			float bhx = b.HalfX + 0.5f * gap, bhz = b.HalfZ + 0.5f * gap;
			float ac = MathF.Cos(a.Yaw), as_ = MathF.Sin(a.Yaw), bc = MathF.Cos(b.Yaw), bs = MathF.Sin(b.Yaw);
			// Axes of a (x: (ac, -as), z: (as, ac)) and b.
			float[] ux = { ac, as_, bc, bs };
			float[] uz = { -as_, ac, -bs, bc };
			float dx = b.X - a.X, dz = b.Z - a.Z;
			for (int i = 0; i < 4; i++)
			{
				float dist = MathF.Abs(dx * ux[i] + dz * uz[i]);
				float ra = ahx * MathF.Abs(ac * ux[i] - as_ * uz[i]) + ahz * MathF.Abs(as_ * ux[i] + ac * uz[i]);
				float rb = bhx * MathF.Abs(bc * ux[i] - bs * uz[i]) + bhz * MathF.Abs(bs * ux[i] + bc * uz[i]);
				if (dist > ra + rb)
					return false;
			}
			return true;
		}

		private static float Range(System.Random rng, float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

		// --- applying ---------------------------------------------------------------------------

		/// <summary>Write the chosen poses into the simulation. Call right after a reset, while everything is at rest.</summary>
		public void Apply()
		{
			var all = new List<Piece>(m_Main);
			all.AddRange(m_Pieces);
			foreach (Piece p in all)
			{
				// The pose is for the footprint centre; the body origin (the entity's pivot) is offset from it by
				// (OffU, OffV) along the footprint's axes, which turn with the yaw.
				float c = MathF.Cos(p.Yaw), s = MathF.Sin(p.Yaw);
				p.TargetX = p.X - (c * p.OffU + s * p.OffV);
				p.TargetZ = p.Z - (-s * p.OffU + c * p.OffV);
				Vector3 pMj = m_HzToMj * (new Vector3(p.TargetX, p.PivotY + 0.002f, p.TargetZ) - m_Origin);
				Quaternion qMj = (m_HzToMj * Planar.Yaw(p.Yaw)).Normalized();
				m_Mujoco.SetQposByIndex(p.QposAdr + 0, pMj.X);
				m_Mujoco.SetQposByIndex(p.QposAdr + 1, pMj.Y);
				m_Mujoco.SetQposByIndex(p.QposAdr + 2, pMj.Z);
				m_Mujoco.SetQposByIndex(p.QposAdr + 3, qMj.W);
				m_Mujoco.SetQposByIndex(p.QposAdr + 4, qMj.X);
				m_Mujoco.SetQposByIndex(p.QposAdr + 5, qMj.Y);
				m_Mujoco.SetQposByIndex(p.QposAdr + 6, qMj.Z);
			}
		}

		/// <summary>Largest distance between where a piece was told to go and where the simulation has it (m).</summary>
		public float MaxPlacementError(out string worst)
		{
			float max = 0f;
			worst = string.Empty;
			foreach (Piece p in m_Pieces)
			{
				float x = m_Mujoco.GetJointPosition(p.QposAdr + 0), y = m_Mujoco.GetJointPosition(p.QposAdr + 1), z = m_Mujoco.GetJointPosition(p.QposAdr + 2);
				Vector3 world = m_Frame.Point(new Vector3(x, y, z));
				float err = MathF.Sqrt((world.X - p.TargetX) * (world.X - p.TargetX) + (world.Z - p.TargetZ) * (world.Z - p.TargetZ));
				if (err > max)
				{
					max = err;
					worst = p.Name;
				}
			}
			return max;
		}

		/// <summary>One line per piece, for the log.</summary>
		public string Describe()
		{
			var sb = new System.Text.StringBuilder();
			foreach (Piece p in m_Main)
				sb.Append($"{p.Name}({p.X:F1},{p.Z:F1},{p.Yaw * Mathf.Rad2Deg:F0}deg) ");
			foreach (Piece p in m_Pieces)
				sb.Append($"{p.Name}({p.X:F1},{p.Z:F1},{p.Yaw * Mathf.Rad2Deg:F0}deg) ");
			return sb.ToString().TrimEnd();
		}
	}
}
