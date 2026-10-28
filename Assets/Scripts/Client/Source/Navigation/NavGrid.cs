// Copied from the RobotCafe example project (Cafe.Robotics), namespace changed.
using System;
using System.Collections.Generic;
using Hazel;

namespace G1RoomNav
{
	/// <summary>
	/// A 2D occupancy and clearance map of the floor, rasterised from the live MuJoCo collision
	/// geometry. Furniture in the cafe is dynamic (chairs and tables have free joints), so the map is
	/// rebuilt from the current geom poses whenever a path is planned rather than baked once.
	///
	/// Every collidable geom whose vertical extent overlaps the robot's height band is projected onto
	/// the floor; a Euclidean distance transform then gives, per cell, the distance to the nearest
	/// obstacle. Paths are planned with A* on that clearance field: cells closer than
	/// <see cref="InscribedRadius"/> are forbidden, cells inside <see cref="ComfortRadius"/> are
	/// penalised, so routes keep a comfortable distance from furniture wherever space allows.
	/// </summary>
	public sealed class NavGrid
	{
		// MuJoCo mjtGeom
		private const int GeomPlane = 0, GeomHField = 1, GeomSphere = 2, GeomCapsule = 3, GeomEllipsoid = 4,
			GeomCylinder = 5, GeomBox = 6;

		private readonly float m_MinX, m_MinZ;
		private readonly bool[] m_Occupied;
		private readonly float[] m_Clearance;
		private readonly List<Vector2> m_Hull = new List<Vector2>(40);
		private readonly List<Vector2> m_Points = new List<Vector2>(40);

		public float CellSize { get; }
		public int Width { get; }
		public int Height { get; }

		/// <summary>Geoms nearer than this to a cell centre make it impassable (m).</summary>
		public float InscribedRadius { get; set; } = 0.26f;
		/// <summary>Beyond this distance from obstacles a cell has no extra cost (m).</summary>
		public float ComfortRadius { get; set; } = 0.60f;
		/// <summary>Cost multiplier at the inscribed radius, fading quadratically to 0 at the comfort radius.</summary>
		public float ProximityPenalty { get; set; } = 6f;
		/// <summary>Only geoms overlapping this height band above the floor are obstacles (m).</summary>
		public float MinObstacleHeight { get; set; } = 0.03f;
		public float MaxObstacleHeight { get; set; } = 1.45f;

		public int ObstacleGeomCount { get; private set; }

		public NavGrid(Vector3 boundsMin, Vector3 boundsMax, float cellSize)
		{
			CellSize = cellSize;
			m_MinX = Math.Min(boundsMin.X, boundsMax.X);
			m_MinZ = Math.Min(boundsMin.Z, boundsMax.Z);
			Width = Math.Max(2, (int)Math.Ceiling(Math.Abs(boundsMax.X - boundsMin.X) / cellSize));
			Height = Math.Max(2, (int)Math.Ceiling(Math.Abs(boundsMax.Z - boundsMin.Z) / cellSize));
			m_Occupied = new bool[Width * Height];
			m_Clearance = new float[Width * Height];
		}

		// --- building ---------------------------------------------------------------------------

		/// <summary>
		/// Rasterise the current collision geometry. Geoms belonging to a body for which
		/// <paramref name="ignoreBody"/> returns true (the robot itself, objects it is carrying),
		/// and geoms for which <paramref name="ignoreGeom"/> returns true, are skipped.
		/// </summary>
		public void Build(MujocoSceneComponent mujoco, in WorldFrame frame, Func<int, bool> ignoreBody, Func<uint, bool>? ignoreGeom = null)
		{
			Array.Clear(m_Occupied);
			ObstacleGeomCount = 0;
			uint geomCount = mujoco.ModelNgeom;
			for (uint g = 0; g < geomCount; g++)
			{
				if (!mujoco.GetGeomDescriptor(g, out int body, out int type, out _, out int contype, out int conaffinity, out Vector3 size))
					continue;
				if ((contype == 0 && conaffinity == 0) || type == GeomPlane || type == GeomHField || ignoreBody(body)
					|| (ignoreGeom != null && ignoreGeom(g)))
					continue;
				if (!mujoco.GetGeomPoseMj(g, out Vector3 pMj, out Quaternion qMj))
					continue;
				if (Rasterize(type, size, frame.Point(pMj), frame.Rotation(qMj)))
					ObstacleGeomCount++;
			}
			ComputeClearance();
		}

		private bool Rasterize(int type, Vector3 size, Vector3 center, Quaternion rotation)
		{
			Vector3 axisX = rotation * new Vector3(1f, 0f, 0f);
			Vector3 axisY = rotation * new Vector3(0f, 1f, 0f);
			Vector3 axisZ = rotation * new Vector3(0f, 0f, 1f);

			switch (type)
			{
				case GeomSphere:
					if (!OverlapsBand(center.Y - size.X, center.Y + size.X))
						return false;
					FillCapsule(center, center, size.X);
					return true;

				case GeomCapsule:
				{
					Vector3 a = center + axisZ * size.Y, b = center - axisZ * size.Y;
					if (!OverlapsBand(Math.Min(a.Y, b.Y) - size.X, Math.Max(a.Y, b.Y) + size.X))
						return false;
					FillCapsule(a, b, size.X);
					return true;
				}

				case GeomCylinder:
				{
					// Two rings circumscribing the end discs.
					const int segments = 16;
					float r = size.X / Mathf.Cos(Mathf.PI / segments);
					m_Points.Clear();
					float minY = float.MaxValue, maxY = float.MinValue;
					for (int end = -1; end <= 1; end += 2)
					{
						Vector3 c = center + axisZ * (size.Y * end);
						for (int i = 0; i < segments; i++)
						{
							float t = Mathf.TwoPI * i / segments;
							Vector3 p = c + axisX * (r * Mathf.Cos(t)) + axisY * (r * Mathf.Sin(t));
							minY = Math.Min(minY, p.Y);
							maxY = Math.Max(maxY, p.Y);
							m_Points.Add(new Vector2(p.X, p.Z));
						}
					}
					if (!OverlapsBand(minY, maxY))
						return false;
					FillConvex(m_Points);
					return true;
				}

				case GeomBox:
				case GeomEllipsoid:   // bounded by its box
				default:              // mesh / sdf: geom_size holds the bounding half-extents
				{
					m_Points.Clear();
					float minY = float.MaxValue, maxY = float.MinValue;
					for (int i = 0; i < 8; i++)
					{
						Vector3 p = center
							+ axisX * ((i & 1) == 0 ? -size.X : size.X)
							+ axisY * ((i & 2) == 0 ? -size.Y : size.Y)
							+ axisZ * ((i & 4) == 0 ? -size.Z : size.Z);
						minY = Math.Min(minY, p.Y);
						maxY = Math.Max(maxY, p.Y);
						m_Points.Add(new Vector2(p.X, p.Z));
					}
					if (!OverlapsBand(minY, maxY))
						return false;
					FillConvex(m_Points);
					return true;
				}
			}
		}

		private bool OverlapsBand(float minY, float maxY) => maxY > MinObstacleHeight && minY < MaxObstacleHeight;

		/// <summary>Mark every cell whose centre lies within <paramref name="radius"/> of segment ab (projected).</summary>
		private void FillCapsule(Vector3 a3, Vector3 b3, float radius)
		{
			var a = new Vector2(a3.X, a3.Z);
			var b = new Vector2(b3.X, b3.Z);
			CellRange(Math.Min(a.X, b.X) - radius, Math.Min(a.Y, b.Y) - radius,
				Math.Max(a.X, b.X) + radius, Math.Max(a.Y, b.Y) + radius, out int x0, out int z0, out int x1, out int z1);
			float r2 = radius * radius;
			for (int z = z0; z <= z1; z++)
			for (int x = x0; x <= x1; x++)
				if (DistanceSquaredToSegment(CellCenter2(x, z), a, b) <= r2)
					m_Occupied[z * Width + x] = true;
		}

		/// <summary>Mark every cell whose centre lies inside the convex hull of <paramref name="points"/>.</summary>
		private void FillConvex(List<Vector2> points)
		{
			ConvexHull(points, m_Hull);
			if (m_Hull.Count < 3)
				return;
			float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
			foreach (Vector2 p in m_Hull)
			{
				minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
				minZ = Math.Min(minZ, p.Y); maxZ = Math.Max(maxZ, p.Y);
			}
			// A footprint thinner than a cell must still block it: pad by half a cell.
			float pad = 0.5f * CellSize;
			CellRange(minX - pad, minZ - pad, maxX + pad, maxZ + pad, out int x0, out int z0, out int x1, out int z1);
			for (int z = z0; z <= z1; z++)
			for (int x = x0; x <= x1; x++)
				if (SignedDistanceToConvex(CellCenter2(x, z), m_Hull) <= pad)
					m_Occupied[z * Width + x] = true;
		}

		/// <summary>Exact Euclidean distance transform (Felzenszwalb &amp; Huttenlocher), in metres.</summary>
		private void ComputeClearance()
		{
			int w = Width, h = Height, n = Math.Max(w, h);
			const float inf = 1e20f;
			var f = new float[n];
			var d = new float[n];
			var v = new int[n];
			var zb = new float[n + 1];
			var grid = new float[w * h];
			for (int i = 0; i < grid.Length; i++)
				grid[i] = m_Occupied[i] ? 0f : inf;

			for (int x = 0; x < w; x++)
			{
				for (int z = 0; z < h; z++) f[z] = grid[z * w + x];
				DistanceTransform1D(f, h, d, v, zb);
				for (int z = 0; z < h; z++) grid[z * w + x] = d[z];
			}
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++) f[x] = grid[z * w + x];
				DistanceTransform1D(f, w, d, v, zb);
				for (int x = 0; x < w; x++) grid[z * w + x] = d[x];
			}
			for (int i = 0; i < grid.Length; i++)
				m_Clearance[i] = grid[i] >= inf * 0.5f ? float.MaxValue : Mathf.Sqrt(grid[i]) * CellSize;
		}

		private static void DistanceTransform1D(float[] f, int n, float[] d, int[] v, float[] z)
		{
			int k = 0;
			v[0] = 0;
			z[0] = float.NegativeInfinity;
			z[1] = float.PositiveInfinity;
			for (int q = 1; q < n; q++)
			{
				float s = Intersection(f, q, v[k]);
				while (s <= z[k])   // z[0] is -inf, so this stops at k == 0
				{
					k--;
					s = Intersection(f, q, v[k]);
				}
				k++;
				v[k] = q;
				z[k] = s;
				z[k + 1] = float.PositiveInfinity;
			}
			k = 0;
			for (int q = 0; q < n; q++)
			{
				while (z[k + 1] < q)
					k++;
				int p = v[k];
				d[q] = (q - p) * (q - p) + f[p];
			}
		}

		/// <summary>Abscissa where the parabolas rooted at q and p intersect.</summary>
		private static float Intersection(float[] f, int q, int p) => ((f[q] + q * q) - (f[p] + p * p)) / (2f * (q - p));

		// --- queries ------------------------------------------------------------------------------

		/// <summary>Distance (m) from <paramref name="p"/> to the nearest obstacle; 0 outside the map.</summary>
		public float ClearanceAt(Vector3 p)
		{
			if (!ToCell(p, out int x, out int z))
				return 0f;
			return m_Clearance[z * Width + x];
		}

		public bool IsTraversable(Vector3 p) => ClearanceAt(p) >= InscribedRadius;

		/// <summary>
		/// Lowest clearance along the straight segment from <paramref name="a"/> to <paramref name="b"/>.
		/// </summary>
		public float MinClearanceAlong(Vector3 a, Vector3 b)
		{
			float length = Planar.Distance(a, b);
			int steps = Math.Max(1, (int)Math.Ceiling(length / (0.5f * CellSize)));
			float min = float.MaxValue;
			for (int i = 0; i <= steps; i++)
				min = Math.Min(min, ClearanceAt(Vector3.Lerp(a, b, (float)i / steps)));
			return min;
		}

		/// <summary>
		/// Plan a path from <paramref name="start"/> to <paramref name="goal"/>. The result starts at
		/// the robot and ends exactly at the goal, smoothed to as few straight legs as clearance allows.
		/// If the start lies inside the inscribed radius (the robot just stepped back from a table), the
		/// search starts from the nearest free cell instead.
		/// </summary>
		public bool FindPath(Vector3 start, Vector3 goal, List<Vector3> path, out string failure)
		{
			path.Clear();
			failure = string.Empty;
			if (!ToCell(start, out int sx, out int sz) || !ToCell(goal, out int gx, out int gz))
			{
				failure = "start or goal outside the navigation bounds";
				return false;
			}
			int startCell = sz * Width + sx, goalCell = gz * Width + gx;
			if (m_Clearance[goalCell] < InscribedRadius)
			{
				failure = $"goal is blocked (clearance {m_Clearance[goalCell]:F2} m)";
				return false;
			}
			if (m_Clearance[startCell] < InscribedRadius && !NearestFreeCell(startCell, 0.6f, out startCell))
			{
				failure = "robot is boxed in (no free cell within 0.6 m)";
				return false;
			}

			var cells = new List<int>();
			if (!AStar(startCell, goalCell, cells))
			{
				failure = "no route to the goal";
				return false;
			}

			// Raw cell path to world points, then shortcut.
			var raw = new List<Vector3>(cells.Count + 2) { Planar.Flatten(start) };
			for (int i = 1; i < cells.Count - 1; i++)
				raw.Add(CellCenter(cells[i] % Width, cells[i] / Width));
			raw.Add(Planar.Flatten(goal));
			Shortcut(raw, path);
			return true;
		}

		private bool AStar(int start, int goal, List<int> result)
		{
			int count = Width * Height;
			var gScore = new float[count];
			var cameFrom = new int[count];
			var closed = new bool[count];
			Array.Fill(gScore, float.MaxValue);
			gScore[start] = 0f;
			cameFrom[start] = -1;
			var open = new PriorityQueue<int, float>();
			open.Enqueue(start, Heuristic(start, goal));
			int gx = goal % Width, gz = goal / Width;

			while (open.TryDequeue(out int current, out _))
			{
				if (current == goal)
				{
					for (int c = goal; c != -1; c = cameFrom[c])
						result.Add(c);
					result.Reverse();
					return true;
				}
				if (closed[current])
					continue;
				closed[current] = true;
				int cx = current % Width, cz = current / Width;
				for (int dz = -1; dz <= 1; dz++)
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dz == 0)
						continue;
					int nx = cx + dx, nz = cz + dz;
					if (nx < 0 || nz < 0 || nx >= Width || nz >= Height)
						continue;
					int next = nz * Width + nx;
					if (closed[next] || m_Clearance[next] < InscribedRadius)
						continue;
					// No corner cutting between two blocked diagonal neighbours.
					if (dx != 0 && dz != 0 && (m_Clearance[cz * Width + nx] < InscribedRadius || m_Clearance[nz * Width + cx] < InscribedRadius))
						continue;
					float step = (dx != 0 && dz != 0 ? 1.41421356f : 1f) * CellSize;
					float tentative = gScore[current] + step * (1f + Penalty(m_Clearance[next]));
					if (tentative >= gScore[next])
						continue;
					gScore[next] = tentative;
					cameFrom[next] = current;
					open.Enqueue(next, tentative + Heuristic(next, goal));
				}
			}
			return false;

			float Heuristic(int cell, int target)
			{
				int ax = Math.Abs(cell % Width - target % Width), az = Math.Abs(cell / Width - target / Width);
				return CellSize * (Math.Max(ax, az) + 0.41421356f * Math.Min(ax, az));
			}
		}

		private float Penalty(float clearance)
		{
			if (clearance >= ComfortRadius)
				return 0f;
			float t = (ComfortRadius - clearance) / (ComfortRadius - InscribedRadius);
			return ProximityPenalty * t * t;
		}

		/// <summary>
		/// Greedy line-of-sight shortcutting. A shortcut is taken only if it keeps at least the
		/// clearance the raw path had over the section it replaces (capped at the comfort radius), so
		/// smoothing never brings the route closer to furniture.
		/// </summary>
		private void Shortcut(List<Vector3> raw, List<Vector3> path)
		{
			path.Add(raw[0]);
			int i = 0;
			while (i < raw.Count - 1)
			{
				int best = i + 1;
				float sectionMin = Math.Min(ClearanceAt(raw[i]), ClearanceAt(raw[i + 1]));
				for (int j = i + 2; j < raw.Count; j++)
				{
					sectionMin = Math.Min(sectionMin, ClearanceAt(raw[j]));
					if (MinClearanceAlong(raw[i], raw[j]) + 1e-4f >= Math.Min(sectionMin, ComfortRadius))
						best = j;
				}
				path.Add(raw[best]);
				i = best;
			}
		}

		private bool NearestFreeCell(int from, float maxDistance, out int found)
		{
			found = from;
			int radius = (int)Math.Ceiling(maxDistance / CellSize);
			int fx = from % Width, fz = from / Width;
			float bestDistance = float.MaxValue;
			for (int dz = -radius; dz <= radius; dz++)
			for (int dx = -radius; dx <= radius; dx++)
			{
				int x = fx + dx, z = fz + dz;
				if (x < 0 || z < 0 || x >= Width || z >= Height)
					continue;
				int c = z * Width + x;
				if (m_Clearance[c] < InscribedRadius)
					continue;
				float d = dx * dx + dz * dz;
				if (d < bestDistance)
				{
					bestDistance = d;
					found = c;
				}
			}
			return bestDistance <= radius * radius;
		}

		/// <summary>
		/// Write the clearance field as a greyscale PGM image (black = obstacle, white = 1 m or more
		/// of clearance; north up = -Z), with the inscribed radius marked in mid grey.
		/// </summary>
		public void SaveImage(string path)
		{
			var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{Width} {Height}\n255\n");
			var pixels = new byte[Width * Height];
			for (int z = 0; z < Height; z++)
			for (int x = 0; x < Width; x++)
			{
				float c = m_Clearance[z * Width + x];
				byte v = c < InscribedRadius ? (byte)(m_Occupied[z * Width + x] ? 0 : 90) : (byte)Math.Min(255f, 120f + 135f * c);
				pixels[z * Width + x] = v;   // row 0 = minimum Z (north)
			}
			using var file = System.IO.File.Create(path);
			file.Write(header, 0, header.Length);
			file.Write(pixels, 0, pixels.Length);
		}

		// --- cell maths ---------------------------------------------------------------------------

		private bool ToCell(Vector3 p, out int x, out int z)
		{
			x = (int)Math.Floor((p.X - m_MinX) / CellSize);
			z = (int)Math.Floor((p.Z - m_MinZ) / CellSize);
			return x >= 0 && z >= 0 && x < Width && z < Height;
		}

		private Vector2 CellCenter2(int x, int z) => new Vector2(m_MinX + (x + 0.5f) * CellSize, m_MinZ + (z + 0.5f) * CellSize);

		private Vector3 CellCenter(int x, int z) => new Vector3(m_MinX + (x + 0.5f) * CellSize, 0f, m_MinZ + (z + 0.5f) * CellSize);

		private void CellRange(float minX, float minZ, float maxX, float maxZ, out int x0, out int z0, out int x1, out int z1)
		{
			x0 = Math.Max(0, (int)Math.Floor((minX - m_MinX) / CellSize));
			z0 = Math.Max(0, (int)Math.Floor((minZ - m_MinZ) / CellSize));
			x1 = Math.Min(Width - 1, (int)Math.Floor((maxX - m_MinX) / CellSize));
			z1 = Math.Min(Height - 1, (int)Math.Floor((maxZ - m_MinZ) / CellSize));
		}

		private static float DistanceSquaredToSegment(Vector2 p, Vector2 a, Vector2 b)
		{
			float abx = b.X - a.X, aby = b.Y - a.Y;
			float len2 = abx * abx + aby * aby;
			float t = len2 > 1e-12f ? Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0f, 1f) : 0f;
			float dx = p.X - (a.X + abx * t), dy = p.Y - (a.Y + aby * t);
			return dx * dx + dy * dy;
		}

		/// <summary>Andrew's monotone chain; output is counter-clockwise without the closing point.</summary>
		private static void ConvexHull(List<Vector2> points, List<Vector2> hull)
		{
			hull.Clear();
			points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
			for (int pass = 0; pass < 2; pass++)
			{
				int floor = hull.Count;
				for (int k = 0; k < points.Count; k++)
				{
					Vector2 p = points[pass == 0 ? k : points.Count - 1 - k];
					while (hull.Count >= floor + 2 && Cross(hull[^2], hull[^1], p) <= 0f)
						hull.RemoveAt(hull.Count - 1);
					hull.Add(p);
				}
				hull.RemoveAt(hull.Count - 1);
			}
		}

		private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

		/// <summary>Signed distance from a point to a counter-clockwise convex polygon (negative inside).</summary>
		private static float SignedDistanceToConvex(Vector2 p, List<Vector2> hull)
		{
			bool inside = true;
			float best = float.MaxValue;
			for (int i = 0; i < hull.Count; i++)
			{
				Vector2 a = hull[i], b = hull[(i + 1) % hull.Count];
				if (Cross(a, b, p) < 0f)
					inside = false;
				best = Math.Min(best, DistanceSquaredToSegment(p, a, b));
			}
			float d = Mathf.Sqrt(best);
			return inside ? -d : d;
		}
	}
}
