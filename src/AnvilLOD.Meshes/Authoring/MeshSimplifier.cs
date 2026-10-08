using System.Numerics;

namespace AnvilLOD.Meshes.Authoring;

/// <summary>
/// Quadric-error mesh simplification (Garland–Heckbert) with half-edge collapses, so every surviving vertex keeps
/// its original UV, normal, tangent and colour. Seams are respected: a collapse is only allowed when every
/// attribute copy of the removed vertex has exactly one matching copy at the target (hard edges and UV seams
/// collapse along themselves instead of tearing). Open borders only collapse along the border, and collapses that
/// would flip a triangle are rejected.
/// </summary>
public static class MeshSimplifier
{
    /// <param name="maxError">Largest allowed deviation in mesh units (the collapse stops beyond it).</param>
    /// <param name="minTriangles">Never go below this many triangles.</param>
    public static LodMeshPart Simplify(LodMeshPart part, float maxError, int minTriangles = 4)
    {
        int nv = part.VertexCount, nt = part.TriangleCount;
        if (nt <= minTriangles) return part;

        // 1) Weld positions (attribute copies of one point share a "pid").
        var pidOf = new int[nv];
        var pidPos = new List<Vector3>();
        var pidVerts = new List<List<int>>();
        var weld = new Dictionary<(int, int, int), int>();
        for (int v = 0; v < nv; v++)
        {
            var p = part.Positions[v];
            var key = ((int)MathF.Round(p.X * 100f), (int)MathF.Round(p.Y * 100f), (int)MathF.Round(p.Z * 100f));
            if (!weld.TryGetValue(key, out var pid))
            {
                pid = pidPos.Count;
                weld[key] = pid;
                pidPos.Add(p);
                pidVerts.Add([]);
            }
            pidOf[v] = pid;
            pidVerts[pid].Add(v);
        }
        int np = pidPos.Count;

        // 2) Triangles (vertex corners), incidence per pid, plane quadrics.
        var tri = new int[nt * 3];
        var alive = new bool[nt];
        var inc = new List<int>[np];
        for (int i = 0; i < np; i++) inc[i] = [];
        var q = new Quadric[np];
        int liveTris = 0;
        for (int t = 0; t < nt; t++)
        {
            int a = part.Triangles[t * 3], b = part.Triangles[t * 3 + 1], c = part.Triangles[t * 3 + 2];
            tri[t * 3] = a; tri[t * 3 + 1] = b; tri[t * 3 + 2] = c;
            int pa = pidOf[a], pb = pidOf[b], pc = pidOf[c];
            if (pa == pb || pb == pc || pa == pc) continue; // degenerate after welding
            alive[t] = true;
            liveTris++;
            inc[pa].Add(t); inc[pb].Add(t); inc[pc].Add(t);
            var n = Vector3.Cross(pidPos[pb] - pidPos[pa], pidPos[pc] - pidPos[pa]);
            float len = n.Length();
            if (len < 1e-12f) continue;
            n /= len;
            var plane = Quadric.FromPlane(n, -Vector3.Dot(n, pidPos[pa]));
            q[pa] += plane; q[pb] += plane; q[pc] += plane;
        }

        // 3) Open borders (edges with one triangle, on welded positions) get a perpendicular constraint plane.
        var edgeCount = new Dictionary<(int, int), int>();
        for (int t = 0; t < nt; t++)
        {
            if (!alive[t]) continue;
            for (int k = 0; k < 3; k++)
            {
                var e = Edge(pidOf[tri[t * 3 + k]], pidOf[tri[t * 3 + (k + 1) % 3]]);
                edgeCount[e] = edgeCount.GetValueOrDefault(e) + 1;
            }
        }
        var border = new HashSet<(int, int)>();
        var onBorder = new bool[np];
        foreach (var (e, count) in edgeCount)
        {
            if (count != 1) continue;
            border.Add(e);
            onBorder[e.Item1] = onBorder[e.Item2] = true;
        }
        for (int t = 0; t < nt; t++)
        {
            if (!alive[t]) continue;
            int pa = pidOf[tri[t * 3]], pb = pidOf[tri[t * 3 + 1]], pc = pidOf[tri[t * 3 + 2]];
            var faceN = Vector3.Normalize(Vector3.Cross(pidPos[pb] - pidPos[pa], pidPos[pc] - pidPos[pa]));
            if (!float.IsFinite(faceN.X)) continue;
            foreach (var (x, y) in new[] { (pa, pb), (pb, pc), (pc, pa) })
            {
                if (!border.Contains(Edge(x, y))) continue;
                var dir = pidPos[y] - pidPos[x];
                var bn = Vector3.Cross(dir, faceN);
                float l = bn.Length();
                if (l < 1e-12f) continue;
                bn /= l;
                var plane = Quadric.FromPlane(bn, -Vector3.Dot(bn, pidPos[x])) * 10f;
                q[x] += plane; q[y] += plane;
            }
        }

        // 4) Collapse cheapest edges first (lazy priority queue with version stamps).
        var version = new int[np];
        var dead = new bool[np];
        var heap = new PriorityQueue<(int From, int To, int Vf, int Vt), float>();
        float maxCost = maxError * maxError;

        void Push(int from, int to)
        {
            if (from == to || dead[from] || dead[to]) return;
            if (onBorder[from] && !border.Contains(Edge(from, to))) return; // border points only slide along the border
            float cost = (q[from] + q[to]).Evaluate(pidPos[to]);
            if (cost <= maxCost) heap.Enqueue((from, to, version[from], version[to]), cost);
        }

        for (int t = 0; t < nt; t++)
        {
            if (!alive[t]) continue;
            for (int k = 0; k < 3; k++)
            {
                int x = pidOf[tri[t * 3 + k]], y = pidOf[tri[t * 3 + (k + 1) % 3]];
                Push(x, y); Push(y, x);
            }
        }

        var remap = new Dictionary<int, int>();
        while (liveTris > minTriangles && heap.TryDequeue(out var c, out _))
        {
            int a = c.From, b = c.To;
            if (dead[a] || dead[b] || version[a] != c.Vf || version[b] != c.Vt) continue;
            if (!TryBuildRemap(a, b)) continue;
            if (Flips(a, b)) continue;

            // Commit: drop triangles that contain both, move the rest of a's triangles to b.
            foreach (var t in inc[a])
            {
                if (!alive[t]) continue;
                bool hasB = false;
                for (int k = 0; k < 3; k++) if (pidOf[tri[t * 3 + k]] == b) hasB = true;
                if (hasB)
                {
                    alive[t] = false;
                    liveTris--;
                    continue;
                }
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[t * 3 + k];
                    if (pidOf[v] == a) tri[t * 3 + k] = remap[v];
                }
                inc[b].Add(t);
            }
            inc[a].Clear();
            dead[a] = true;
            q[b] += q[a];
            if (onBorder[a]) onBorder[b] = true;
            // Border edges a-x become b-x.
            foreach (var t in inc[b])
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int x = pidOf[tri[t * 3 + k]];
                    if (x != b && border.Remove(Edge(a, x))) border.Add(Edge(b, x));
                }
            }
            inc[b].RemoveAll(t => !alive[t]);
            version[b]++;

            var neighbours = new HashSet<int>();
            foreach (var t in inc[b])
                for (int k = 0; k < 3; k++) neighbours.Add(pidOf[tri[t * 3 + k]]);
            neighbours.Remove(b);
            foreach (var n in neighbours) { Push(b, n); Push(n, b); }
        }

        // 5) Compact.
        var newIndex = new int[nv];
        Array.Fill(newIndex, -1);
        var pos = new List<Vector3>(); var uv = new List<Vector2>(); var nrm = new List<Vector3>();
        var tan = new List<Vector3>(); var bit = new List<Vector3>(); var col = part.Colors is null ? null : new List<uint>();
        var tris = new List<ushort>();
        for (int t = 0; t < nt; t++)
        {
            if (!alive[t]) continue;
            for (int k = 0; k < 3; k++)
            {
                int v = tri[t * 3 + k];
                if (newIndex[v] < 0)
                {
                    newIndex[v] = pos.Count;
                    pos.Add(part.Positions[v]); uv.Add(part.UVs[v]); nrm.Add(part.Normals[v]);
                    tan.Add(part.Tangents[v]); bit.Add(part.Bitangents[v]);
                    col?.Add(part.Colors![v]);
                }
                tris.Add((ushort)newIndex[v]);
            }
        }
        return new LodMeshPart
        {
            Material = part.Material,
            Positions = [.. pos], UVs = [.. uv], Normals = [.. nrm], Tangents = [.. tan], Bitangents = [.. bit],
            Colors = col?.ToArray(), Triangles = [.. tris],
        };

        // Every attribute copy of a that is still used must connect to exactly one copy of b.
        bool TryBuildRemap(int a, int b)
        {
            remap.Clear();
            var candidates = new Dictionary<int, int>(); // va -> vb (or -1 if ambiguous)
            foreach (var t in inc[a])
            {
                if (!alive[t]) continue;
                int va = -1, vb = -1;
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[t * 3 + k];
                    if (pidOf[v] == a) va = v;
                    else if (pidOf[v] == b) vb = v;
                }
                if (va < 0) continue;
                if (vb < 0) { candidates.TryAdd(va, int.MinValue); continue; }
                if (candidates.TryGetValue(va, out var prev) && prev != int.MinValue && prev != vb) candidates[va] = -1;
                else candidates[va] = vb;
            }
            foreach (var (va, vb) in candidates)
            {
                if (vb < 0) return false; // ambiguous, or this copy never touches b
                remap[va] = vb;
            }
            return remap.Count > 0;
        }

        bool Flips(int a, int b)
        {
            var target = pidPos[b];
            foreach (var t in inc[a])
            {
                if (!alive[t]) continue;
                int p0 = pidOf[tri[t * 3]], p1 = pidOf[tri[t * 3 + 1]], p2 = pidOf[tri[t * 3 + 2]];
                if (p0 == b || p1 == b || p2 == b) continue;
                var before = Vector3.Cross(pidPos[p1] - pidPos[p0], pidPos[p2] - pidPos[p0]);
                var q0 = p0 == a ? target : pidPos[p0];
                var q1 = p1 == a ? target : pidPos[p1];
                var q2 = p2 == a ? target : pidPos[p2];
                var after = Vector3.Cross(q1 - q0, q2 - q0);
                float la = after.Length(), lb = before.Length();
                if (la < 1e-9f) return true; // would become a sliver
                if (lb > 1e-9f && Vector3.Dot(before / lb, after / la) < 0.3f) return true;
            }
            return false;
        }
    }

    private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>Symmetric 4x4 error quadric (10 unique terms).</summary>
    private readonly struct Quadric(double a2, double ab, double ac, double ad, double b2, double bc, double bd, double c2, double cd, double d2)
    {
        private readonly double _a2 = a2, _ab = ab, _ac = ac, _ad = ad, _b2 = b2, _bc = bc, _bd = bd, _c2 = c2, _cd = cd, _d2 = d2;

        public static Quadric FromPlane(Vector3 n, float d) =>
            new(n.X * n.X, n.X * n.Y, n.X * n.Z, n.X * d, n.Y * n.Y, n.Y * n.Z, n.Y * d, n.Z * n.Z, n.Z * d, (double)d * d);

        public static Quadric operator +(Quadric x, Quadric y) =>
            new(x._a2 + y._a2, x._ab + y._ab, x._ac + y._ac, x._ad + y._ad, x._b2 + y._b2, x._bc + y._bc, x._bd + y._bd, x._c2 + y._c2, x._cd + y._cd, x._d2 + y._d2);

        public static Quadric operator *(Quadric x, float s) =>
            new(x._a2 * s, x._ab * s, x._ac * s, x._ad * s, x._b2 * s, x._bc * s, x._bd * s, x._c2 * s, x._cd * s, x._d2 * s);

        public float Evaluate(Vector3 v)
        {
            double x = v.X, y = v.Y, z = v.Z;
            double e = _a2 * x * x + 2 * _ab * x * y + 2 * _ac * x * z + 2 * _ad * x
                     + _b2 * y * y + 2 * _bc * y * z + 2 * _bd * y
                     + _c2 * z * z + 2 * _cd * z + _d2;
            return (float)Math.Max(0, e);
        }
    }
}
