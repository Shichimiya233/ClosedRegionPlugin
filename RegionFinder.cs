using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ClosedRegionPlugin.Core
{
    public static class RegionFinder
    {
        private const double Eps = Constants.PointMergeTolerance;

        // ═══════════════════════════════════════════════════════
        //  最小封闭区域：平面图中所有有界面
        // ═══════════════════════════════════════════════════════
        public static List<CurveLoop> FindMinRegions(List<Curve> preprocessedCurves)
        {
            var graph = PlanarGraph.Build(preprocessedCurves);
            var result = new List<CurveLoop>();

            foreach (var face in graph.Faces)
            {
                if (face.IsOuterFace) continue;
                // 直接内联：原 FaceToLoop 只是 BuildCurveLoop(face.GetEdges()) 的一层包装
                var loop = BuildCurveLoop(face.GetEdges());
                if (loop != null) result.Add(loop);
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════
        //  最大封闭区域：每个连通分量的外轮廓
        // ═══════════════════════════════════════════════════════
        public static List<CurveLoop> FindMaxRegions(List<Curve> preprocessedCurves)
        {
            var graph = PlanarGraph.Build(preprocessedCurves);

            var innerFaces = graph.Faces
                .Where(f => !f.IsOuterFace && Math.Abs(f.SignedArea) > 1e-6)
                .ToList();

            if (innerFaces.Count == 0) return new List<CurveLoop>();

            var faceSet = new HashSet<Face>(innerFaces);

            // ── 建立内部面邻接图 ──────────────────────────────
            var adjacency = innerFaces.ToDictionary(f => f, _ => new HashSet<Face>());

            foreach (var he in graph.HalfEdges)
            {
                if (he.Face == null || he.Twin?.Face == null) continue;
                if (!faceSet.Contains(he.Face)) continue;
                if (!faceSet.Contains(he.Twin.Face)) continue;
                if (he.Face == he.Twin.Face) continue;

                adjacency[he.Face].Add(he.Twin.Face);
                adjacency[he.Twin.Face].Add(he.Face);
            }

            // ── BFS 分连通分量 ────────────────────────────────
            var visited = new HashSet<Face>();
            var components = new List<HashSet<Face>>();

            foreach (var startFace in innerFaces)
            {
                if (visited.Contains(startFace)) continue;

                var component = new HashSet<Face>();
                var queue = new Queue<Face>();
                queue.Enqueue(startFace);
                visited.Add(startFace);

                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    component.Add(f);
                    foreach (var nb in adjacency[f])
                    {
                        if (visited.Contains(nb)) continue;
                        visited.Add(nb);
                        queue.Enqueue(nb);
                    }
                }
                components.Add(component);
            }

            // ── 对每个连通分量提取外轮廓 ─────────────────────
            var result = new List<CurveLoop>();

            foreach (var component in components)
            {
                var boundaryEdges = new List<HalfEdge>();
                foreach (var face in component)
                    foreach (var he in face.GetEdges())
                    {
                        bool twinInside = he.Twin != null
                            && he.Twin.Face != null
                            && component.Contains(he.Twin.Face);
                        if (!twinInside) boundaryEdges.Add(he);
                    }

                var loops = AssembleLoops(boundaryEdges);
                if (loops.Count == 0) continue;

                result.Add(loops.OrderByDescending(l => AbsArea(l)).First());
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════
        //  边界半边串联成 CurveLoop 列表
        // ═══════════════════════════════════════════════════════
        private static List<CurveLoop> AssembleLoops(List<HalfEdge> edges)
        {
            var startMap = new Dictionary<string, List<HalfEdge>>();
            foreach (var he in edges)
            {
                string key = PtKey(he.Origin.Position);
                if (!startMap.ContainsKey(key))
                    startMap[key] = new List<HalfEdge>();
                startMap[key].Add(he);
            }

            var used = new HashSet<HalfEdge>();
            var result = new List<CurveLoop>();

            foreach (var startEdge in edges)
            {
                if (used.Contains(startEdge)) continue;

                var loopEdges = new List<HalfEdge>();
                var current = startEdge;
                int guard = 0;

                while (!used.Contains(current) && guard++ < 200000)
                {
                    used.Add(current);
                    loopEdges.Add(current);

                    string nextKey = PtKey(current.Destination.Position);
                    if (!startMap.TryGetValue(nextKey, out var candidates)) break;

                    var available = candidates.Where(c => !used.Contains(c)).ToList();
                    if (available.Count == 0) break;

                    current = available.Count == 1
                        ? available[0]
                        : PickNextBoundaryEdge(current, available);
                }

                if (loopEdges.Count < 2) continue;

                XYZ loopStart = loopEdges[0].Origin.Position;
                XYZ loopEnd = loopEdges[loopEdges.Count - 1].Destination?.Position;
                if (loopEnd == null || loopStart.DistanceTo(loopEnd) > Eps * 3) continue;

                var loop = BuildCurveLoop(loopEdges);
                if (loop != null) result.Add(loop);
            }

            return result;
        }

        private static HalfEdge PickNextBoundaryEdge(
            HalfEdge currentEdge, List<HalfEdge> candidates)
        {
            double reverseAngle = NormalizeAngle(currentEdge.OutgoingAngle + Math.PI);
            HalfEdge best = null;
            double bestDiff = double.MaxValue;

            foreach (var c in candidates)
            {
                double diff = NormalizeAngle(reverseAngle - c.OutgoingAngle);
                if (diff < bestDiff) { bestDiff = diff; best = c; }
            }
            return best ?? candidates[0];
        }

        private static double NormalizeAngle(double angle)
        {
            const double TwoPI = Math.PI * 2;
            angle %= TwoPI;
            if (angle < 0) angle += TwoPI;
            return angle;
        }

        private static CurveLoop BuildCurveLoop(List<HalfEdge> edges)
        {
            if (edges == null || edges.Count == 0) return null;
            try
            {
                var loop = new CurveLoop();
                foreach (var he in edges)
                    if (he.Curve != null)
                        loop.Append(he.Curve);
                return loop;
            }
            catch { return null; }
        }

        private static double AbsArea(CurveLoop loop)
        {
            double area = 0;
            foreach (var c in loop)
            {
                double p0 = c.GetEndParameter(0), p1 = c.GetEndParameter(1);
                const int segs = 16;
                XYZ prev = c.Evaluate(p0, false);
                for (int i = 1; i <= segs; i++)
                {
                    XYZ curr = c.Evaluate(p0 + (p1 - p0) * i / segs, false);
                    area += prev.X * curr.Y - curr.X * prev.Y;
                    prev = curr;
                }
            }
            return Math.Abs(area) / 2.0;
        }

        private static string PtKey(XYZ pt)
        {
            long ix = (long)Math.Floor(pt.X / Eps);
            long iy = (long)Math.Floor(pt.Y / Eps);
            return $"{ix},{iy}";
        }
    }
}