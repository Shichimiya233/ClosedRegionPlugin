using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ClosedRegionPlugin.Core
{
    /// <summary>
    /// 曲线预处理器（完全基于解析几何，不依赖 Revit Intersect API）
    /// 流程：过滤 → 两两求交 → 注册打断参数 → 拆分 → 端点合并 → 去重
    /// </summary>
    public static class CurvePreprocessor
    {
        private const double Eps = Constants.PointMergeTolerance;
        private const double TJTol = Constants.TJunctionTolerance;
        private const double InteriorT = 1e-6;

        public static List<Curve> Process(IList<Curve> inputCurves)
        {
            var curves = inputCurves.Where(c => c != null && c.Length > 1e-6).ToList();
            if (curves.Count == 0) return new List<Curve>();

            var splitParams = Enumerable.Range(0, curves.Count)
                                        .Select(_ => new SortedSet<double>())
                                        .ToList();

            for (int i = 0; i < curves.Count; i++)
                for (int j = i + 1; j < curves.Count; j++)
                    ComputeSplitParams(curves[i], i, curves[j], j, splitParams);

            var allSubs = new List<Curve>();
            for (int i = 0; i < curves.Count; i++)
                allSubs.AddRange(SplitCurveAtParams(curves[i], splitParams[i]));

            allSubs = MergeNearbyEndpoints(allSubs);
            allSubs = RemoveDuplicatesAndDegenerate(allSubs);
            return allSubs;
        }

        // ═══════════════════════════════════════════════════════
        //  分派
        // ═══════════════════════════════════════════════════════
        private static void ComputeSplitParams(
            Curve c1, int i1, Curve c2, int i2, List<SortedSet<double>> sp)
        {
            bool l1 = c1 is Line, l2 = c2 is Line;
            bool a1 = c1 is Arc, a2 = c2 is Arc;

            if (l1 && l2) IntersectLineLine(c1, i1, c2, i2, sp);
            else if (l1 && a2) IntersectLineArc((Line)c1, i1, (Arc)c2, i2, sp);
            else if (a1 && l2) IntersectLineArc((Line)c2, i2, (Arc)c1, i1, sp);
            else IntersectByRevitApi(c1, i1, c2, i2, sp);

            // T形接头补充检测（对所有类型组合都执行）
            ProjectEndpointsOnCurve(c1, i1, c2, sp);
            ProjectEndpointsOnCurve(c2, i2, c1, sp);
        }

        // ═══════════════════════════════════════════════════════
        //  直线 × 直线  —  2D 行列式法
        // ═══════════════════════════════════════════════════════
        private static void IntersectLineLine(
            Curve c1, int i1, Curve c2, int i2, List<SortedSet<double>> sp)
        {
            XYZ a = c1.GetEndPoint(0), b = c1.GetEndPoint(1);
            XYZ c = c2.GetEndPoint(0), d = c2.GetEndPoint(1);

            double dx1 = b.X - a.X, dy1 = b.Y - a.Y;
            double dx2 = d.X - c.X, dy2 = d.Y - c.Y;
            double det = dx1 * dy2 - dy1 * dx2;

            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            double len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);

            if (Math.Abs(det) < 1e-8 * len1 * len2)
            {
                // 平行：检测是否共线后处理重叠
                double ex0 = c.X - a.X, ey0 = c.Y - a.Y;
                double distToLine = Math.Abs(ex0 * dy1 - ey0 * dx1) / (len1 + 1e-20);
                if (distToLine < Eps)
                    HandleCollinearLines(c1, i1, c2, i2, sp);
                return;
            }

            double ex = c.X - a.X, ey = c.Y - a.Y;
            RegisterLineRatio(c1, i1, (ex * dy2 - ey * dx2) / det, sp);
            RegisterLineRatio(c2, i2, (ex * dy1 - ey * dx1) / det, sp);
        }

        // ═══════════════════════════════════════════════════════
        //  共线重叠处理
        // ═══════════════════════════════════════════════════════
        private static void HandleCollinearLines(
            Curve c1, int i1, Curve c2, int i2, List<SortedSet<double>> sp)
        {
            XYZ a = c1.GetEndPoint(0), b = c1.GetEndPoint(1);
            XYZ c = c2.GetEndPoint(0), d = c2.GetEndPoint(1);

            double dx1 = b.X - a.X, dy1 = b.Y - a.Y;
            double dx2 = d.X - c.X, dy2 = d.Y - c.Y;
            double len1sq = dx1 * dx1 + dy1 * dy1;
            double len2sq = dx2 * dx2 + dy2 * dy2;

            // c2 的两端点 → 投影到 c1
            if (len1sq > 1e-20)
            {
                double t0 = ((c.X - a.X) * dx1 + (c.Y - a.Y) * dy1) / len1sq;
                double t1 = ((d.X - a.X) * dx1 + (d.Y - a.Y) * dy1) / len1sq;
                RegisterLineRatio(c1, i1, t0, sp);
                RegisterLineRatio(c1, i1, t1, sp);
            }

            // c1 的两端点 → 投影到 c2
            if (len2sq > 1e-20)
            {
                double s0 = ((a.X - c.X) * dx2 + (a.Y - c.Y) * dy2) / len2sq;
                double s1 = ((b.X - c.X) * dx2 + (b.Y - c.Y) * dy2) / len2sq;
                RegisterLineRatio(c2, i2, s0, sp);
                RegisterLineRatio(c2, i2, s1, sp);
            }
        }

        // ═══════════════════════════════════════════════════════
        //  直线 × 弧线  —  2D 二次方程法
        // ═══════════════════════════════════════════════════════
        private static void IntersectLineArc(
            Line line, int lineIdx, Arc arc, int arcIdx, List<SortedSet<double>> sp)
        {
            XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
            XYZ cen = arc.Center;
            double R = arc.Radius;

            double dx = b.X - a.X, dy = b.Y - a.Y;
            double ex = a.X - cen.X, ey = a.Y - cen.Y;

            double A2 = dx * dx + dy * dy;
            double B2 = 2.0 * (ex * dx + ey * dy);
            double C2 = ex * ex + ey * ey - R * R;
            double disc = B2 * B2 - 4 * A2 * C2;

            if (disc < 0 || A2 < 1e-20) return;

            // 优化：避免 foreach new[] 的数组分配，改为手写两次
            double sqD = Math.Sqrt(disc);
            for (int sign = -1; sign <= 1; sign += 2)
            {
                double t = (-B2 + sign * sqD) / (2 * A2);
                if (t < -InteriorT || t > 1.0 + InteriorT) continue;

                XYZ ptOnArc = new XYZ(a.X + t * dx, a.Y + t * dy, cen.Z);
                try
                {
                    IntersectionResult proj = arc.Project(ptOnArc);
                    if (proj == null || proj.Distance > TJTol) continue;
                    RegisterLineRatio(line, lineIdx, t, sp);
                    RegisterCurveParam(arc, arcIdx, proj.Parameter, sp);
                }
                catch { }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  回退：Revit API（弧×弧、样条等）
        // ═══════════════════════════════════════════════════════
        private static void IntersectByRevitApi(
            Curve c1, int i1, Curve c2, int i2, List<SortedSet<double>> sp)
        {
            try
            {
                SetComparisonResult res = c1.Intersect(c2, out IntersectionResultArray arr);
                if (res == SetComparisonResult.Disjoint || arr == null) return;
                foreach (IntersectionResult ir in arr)
                {
                    RegisterCurveParamByProjection(c1, i1, ir.XYZPoint, sp);
                    RegisterCurveParamByProjection(c2, i2, ir.XYZPoint, sp);
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════
        //  T形接头：把 src 的每个端点投影到 target 内部
        // ═══════════════════════════════════════════════════════
        private static void ProjectEndpointsOnCurve(
            Curve src, int targetIdx, Curve target, List<SortedSet<double>> sp)
        {
            for (int k = 0; k <= 1; k++)
            {
                XYZ ep = src.GetEndPoint(k);

                if (target is Line tLine)
                {
                    XYZ a = tLine.GetEndPoint(0), b = tLine.GetEndPoint(1);
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double len2 = dx * dx + dy * dy;
                    if (len2 < 1e-20) continue;

                    double t = ((ep.X - a.X) * dx + (ep.Y - a.Y) * dy) / len2;
                    if (t <= InteriorT || t >= 1.0 - InteriorT) continue;

                    double projX = a.X + t * dx, projY = a.Y + t * dy;
                    if (Dist2D(ep.X, ep.Y, projX, projY) > TJTol) continue;

                    RegisterLineRatio(tLine, targetIdx, t, sp);
                }
                else
                {
                    RegisterCurveParamByProjection(target, targetIdx, ep, sp);
                }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  参数注册辅助
        // ═══════════════════════════════════════════════════════
        private static void RegisterLineRatio(
            Curve line, int idx, double t, List<SortedSet<double>> sp)
        {
            if (t <= InteriorT || t >= 1.0 - InteriorT) return;
            double p0 = line.GetEndParameter(0), p1 = line.GetEndParameter(1);
            sp[idx].Add(p0 + t * (p1 - p0));
        }

        private static void RegisterCurveParam(
            Curve curve, int idx, double param, List<SortedSet<double>> sp)
        {
            double p0 = curve.GetEndParameter(0), p1 = curve.GetEndParameter(1);
            double range = Math.Abs(p1 - p0);
            double iEps = range * 1e-4 + 1e-10;
            if (param > p0 + iEps && param < p1 - iEps)
                sp[idx].Add(param);
        }

        private static void RegisterCurveParamByProjection(
            Curve curve, int idx, XYZ pt, List<SortedSet<double>> sp)
        {
            try
            {
                XYZ pt2 = new XYZ(pt.X, pt.Y, curve.GetEndPoint(0).Z);
                IntersectionResult proj = curve.Project(pt2);
                if (proj == null || proj.Distance > TJTol) return;
                RegisterCurveParam(curve, idx, proj.Parameter, sp);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════
        //  曲线拆分
        // ═══════════════════════════════════════════════════════
        private static List<Curve> SplitCurveAtParams(Curve curve, SortedSet<double> rawParams)
        {
            double p0 = curve.GetEndParameter(0), p1 = curve.GetEndParameter(1);
            double range = Math.Abs(p1 - p0);

            var all = new List<double> { p0 };
            foreach (double sp in rawParams)
                if (sp > p0 + 1e-10 && sp < p1 - 1e-10)
                    all.Add(sp);
            all.Add(p1);

            var clean = new List<double> { all[0] };
            for (int i = 1; i < all.Count; i++)
                if (all[i] - clean[clean.Count - 1] > range * 1e-5 + 1e-12)
                    clean.Add(all[i]);

            if (clean.Count < 2) return new List<Curve> { curve };

            var result = new List<Curve>();
            for (int i = 0; i < clean.Count - 1; i++)
            {
                Curve sub = TrimCurve(curve, clean[i], clean[i + 1]);
                if (sub != null) result.Add(sub);
            }
            return result.Count > 0 ? result : new List<Curve> { curve };
        }

        private static Curve TrimCurve(Curve curve, double p0, double p1)
        {
            try
            {
                Curve c = curve.Clone();
                c.MakeBound(p0, p1);
                return c.Length > 1e-6 ? c : null;
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════════
        //  端点合并（2D 距离，Z 保留原值）
        // ═══════════════════════════════════════════════════════
        private static List<Curve> MergeNearbyEndpoints(List<Curve> curves)
        {
            var pointMap = new Dictionary<string, XYZ>();

            XYZ Canonical(XYZ pt)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        string nk = GridKey2D(pt.X + dx * Eps, pt.Y + dy * Eps);
                        if (pointMap.TryGetValue(nk, out XYZ existing)
                            && Dist2D(existing.X, existing.Y, pt.X, pt.Y) <= Eps)
                            return existing;
                    }
                string key = GridKey2D(pt.X, pt.Y);
                pointMap[key] = pt;
                return pt;
            }

            var result = new List<Curve>();
            foreach (var curve in curves)
            {
                XYZ s = Canonical(curve.GetEndPoint(0));
                XYZ e = Canonical(curve.GetEndPoint(1));
                if (Dist2D(s.X, s.Y, e.X, e.Y) < Eps) continue;
                Curve rebuilt = RebuildWithEndpoints(curve, s, e);
                if (rebuilt != null) result.Add(rebuilt);
            }
            return result;
        }

        private static Curve RebuildWithEndpoints(Curve original, XYZ newS, XYZ newE)
        {
            try
            {
                if (original is Line)
                {
                    if (Dist2D(newS.X, newS.Y, newE.X, newE.Y) < 1e-6) return null;
                    return Line.CreateBound(newS, newE);
                }
                if (original is Arc arc)
                {
                    double mid = (arc.GetEndParameter(0) + arc.GetEndParameter(1)) / 2.0;
                    XYZ midPt = arc.Evaluate(mid, false);
                    try { return Arc.Create(newS, newE, midPt); }
                    catch { return original; }
                }
                return original; // 样条等保留原曲线
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════════
        //  去重 & 去退化
        // ═══════════════════════════════════════════════════════
        private static List<Curve> RemoveDuplicatesAndDegenerate(List<Curve> curves)
        {
            var result = new List<Curve>();
            var seen = new HashSet<string>();
            foreach (var c in curves)
                if (c.Length > 1e-6 && seen.Add(CurveKey(c)))
                    result.Add(c);
            return result;
        }

        private static string CurveKey(Curve c)
        {
            string tp = c is Line ? "L" : c is Arc ? "A" : "O";
            string k1 = GridKey2D(c.GetEndPoint(0).X, c.GetEndPoint(0).Y);
            string k2 = GridKey2D(c.GetEndPoint(1).X, c.GetEndPoint(1).Y);
            return tp + ":" + (string.Compare(k1, k2, StringComparison.Ordinal) <= 0
                ? k1 + "|" + k2 : k2 + "|" + k1);
        }

        private static double Dist2D(double x1, double y1, double x2, double y2)
            => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

        private static string GridKey2D(double x, double y)
        {
            long ix = (long)Math.Floor(x / Eps);
            long iy = (long)Math.Floor(y / Eps);
            return $"{ix},{iy}";
        }
    }
}