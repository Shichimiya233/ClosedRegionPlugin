using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ClosedRegionPlugin.Core
{
    public class Vertex
    {
        public int Id { get; set; }
        public XYZ Position { get; set; }
        public List<HalfEdge> OutgoingEdges { get; } = new List<HalfEdge>();
    }

    public class HalfEdge
    {
        public int Id { get; set; }
        public Vertex Origin { get; set; }
        public Vertex Destination => Twin?.Origin;
        public HalfEdge Twin { get; set; }
        public HalfEdge Next { get; set; }
        public Face Face { get; set; }
        public Curve Curve { get; set; }
        public bool Visited { get; set; }

        /// <summary>从 Origin 出发的切线角度（弧度），用于 CCW 排序</summary>
        public double OutgoingAngle
        {
            get
            {
                if (Curve != null)
                {
                    try
                    {
                        var d = Curve.ComputeDerivatives(Curve.GetEndParameter(0), false);
                        var t = d.BasisX;
                        if (t.GetLength() > 1e-10) return Math.Atan2(t.Y, t.X);
                    }
                    catch { }
                }
                if (Destination != null)
                {
                    XYZ dir = Destination.Position - Origin.Position;
                    if (dir.GetLength() > 1e-10) return Math.Atan2(dir.Y, dir.X);
                }
                return 0;
            }
        }

        public List<HalfEdge> GetFaceEdges()
        {
            var list = new List<HalfEdge>();
            var cur = this;
            int guard = 0;
            do { list.Add(cur); cur = cur.Next; if (++guard > 200000) break; }
            while (cur != null && cur != this);
            return list;
        }
    }

    public class Face
    {
        public int Id { get; set; }
        public HalfEdge OuterComponent { get; set; }
        public bool IsOuterFace { get; set; }
        public double SignedArea { get; set; }

        public List<HalfEdge> GetEdges() =>
            OuterComponent?.GetFaceEdges() ?? new List<HalfEdge>();
    }

    public class PlanarGraph
    {
        public List<Vertex> Vertices { get; } = new List<Vertex>();
        public List<HalfEdge> HalfEdges { get; } = new List<HalfEdge>();
        public List<Face> Faces { get; } = new List<Face>();

        private int _vid, _heid, _fid;
        private const double Eps = Constants.PointMergeTolerance;

        public static PlanarGraph Build(List<Curve> curves)
        {
            var g = new PlanarGraph();
            g.BuildInternal(curves);
            return g;
        }

        private void BuildInternal(List<Curve> curves)
        {
            var vertexLookup = new Dictionary<string, Vertex>();

            // ── 顶点查找/创建（2D 距离合并，Z 保留原值） ──────
            Vertex GetOrCreate(XYZ pos)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        string nk = GridKey2D(pos.X + dx * Eps, pos.Y + dy * Eps);
                        if (vertexLookup.TryGetValue(nk, out var existing)
                            && Dist2D(existing.Position, pos) <= Eps)
                            return existing;
                    }
                var v = new Vertex { Id = _vid++, Position = pos };
                vertexLookup[GridKey2D(pos.X, pos.Y)] = v;
                Vertices.Add(v);
                return v;
            }

            // ── 步骤 1：建立半边对 ─────────────────────────────
            foreach (var curve in curves)
            {
                XYZ s = curve.GetEndPoint(0);
                XYZ e = curve.GetEndPoint(1);
                if (Dist2D(s, e) < Eps * 0.5) continue;

                var vS = GetOrCreate(s);
                var vE = GetOrCreate(e);
                if (vS == vE) continue;

                Curve rev;
                try { rev = curve.CreateReversed(); } catch { continue; }

                var he1 = new HalfEdge { Id = _heid++, Origin = vS, Curve = curve };
                var he2 = new HalfEdge { Id = _heid++, Origin = vE, Curve = rev };
                he1.Twin = he2; he2.Twin = he1;
                vS.OutgoingEdges.Add(he1);
                vE.OutgoingEdges.Add(he2);
                HalfEdges.Add(he1);
                HalfEdges.Add(he2);
            }

            // ── 步骤 2：出边按 CCW 角度排序 ────────────────────
            foreach (var v in Vertices)
                v.OutgoingEdges.Sort((a, b) => a.OutgoingAngle.CompareTo(b.OutgoingAngle));

            // ── 步骤 3：链接 Next（最右转规则） ────────────────
            // 优化：为每个顶点预建 HalfEdge→index 字典，避免 O(n) IndexOf
            foreach (var v in Vertices)
            {
                var edges = v.OutgoingEdges;
                int n = edges.Count;
                if (n == 0) continue;

                // 为该顶点出边建索引表
                var indexMap = new Dictionary<HalfEdge, int>(n);
                for (int i = 0; i < n; i++) indexMap[edges[i]] = i;

                // 遍历所有到达该顶点的半边（即所有出边的 Twin）
                foreach (var he in edges)
                {
                    // he.Twin 到达本顶点 v，它的 Next 是本顶点出边中 he 前一条
                    if (!indexMap.TryGetValue(he, out int k)) continue;
                    he.Twin.Next = edges[(k - 1 + n) % n];
                }
            }

            // ── 步骤 4：枚举面 ─────────────────────────────────
            foreach (var he in HalfEdges) he.Visited = false;

            foreach (var start in HalfEdges)
            {
                if (start.Visited || start.Next == null) continue;

                var face = new Face { Id = _fid++, OuterComponent = start };
                var cur = start;
                int guard = 0;
                do
                {
                    cur.Visited = true;
                    cur.Face = face;
                    cur = cur.Next;
                    if (++guard > 200000) break;
                } while (cur != null && !cur.Visited);

                double area = ComputeSignedArea(face);
                face.SignedArea = area;
                face.IsOuterFace = area <= 0;
                Faces.Add(face);
            }
        }

        private static double ComputeSignedArea(Face face)
        {
            double area = 0;
            foreach (var he in face.GetEdges())
            {
                if (he.Curve == null) continue;
                const int segs = 32;
                double p0 = he.Curve.GetEndParameter(0), p1 = he.Curve.GetEndParameter(1);
                XYZ prev = he.Curve.Evaluate(p0, false);
                for (int i = 1; i <= segs; i++)
                {
                    XYZ curr = he.Curve.Evaluate(p0 + (p1 - p0) * i / segs, false);
                    area += prev.X * curr.Y - curr.X * prev.Y;
                    prev = curr;
                }
            }
            return area / 2.0;
        }

        private static double Dist2D(XYZ a, XYZ b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private static string GridKey2D(double x, double y)
        {
            long ix = (long)Math.Floor(x / Eps);
            long iy = (long)Math.Floor(y / Eps);
            return $"{ix},{iy}";
        }
    }
}