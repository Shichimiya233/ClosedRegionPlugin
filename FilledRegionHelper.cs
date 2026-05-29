using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ClosedRegionPlugin.Core
{
    public static class FilledRegionHelper
    {
        // ═══════════════════════════════════════════════════════
        //  删除当前视图中所有指定类型名的填充区域
        //  最直接的方案：按类型名 + OwnerViewId 两个条件过滤后全删
        // ═══════════════════════════════════════════════════════
        public static void DeleteExistingRegions(
            Document doc, ElementId viewId, string typeName)
        {
            // 1. 找到目标类型的 ID
            var targetType = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault(t => t.Name == typeName);

            if (targetType == null) return; // 类型不存在，说明从未创建过，直接跳过

            ElementId targetTypeId = targetType.Id;

            // 2. 全文档找所有 FilledRegion，按视图 + 类型 ID 双重过滤
            var toDelete = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegion))
                .Cast<FilledRegion>()
                .Where(fr => fr.OwnerViewId == viewId
                          && fr.GetTypeId() == targetTypeId)
                .Select(fr => fr.Id)
                .ToList();

            if (toDelete.Count > 0)
                doc.Delete(toDelete);
        }

        // ═══════════════════════════════════════════════════════
        //  批量创建填充区域
        // ═══════════════════════════════════════════════════════
        public static List<ElementId> CreateFilledRegions(
            Document doc, ElementId viewId, List<CurveLoop> loops,
            string typeName, byte r, byte g, byte b)
        {
            ElementId typeId = GetOrCreateFilledRegionType(doc, typeName, r, g, b);
            var created = new List<ElementId>();

            foreach (var loop in loops)
            {
                try
                {
                    if (!IsValidLoop(loop)) continue;
                    FilledRegion fr = TryCreateFilledRegion(doc, typeId, viewId, loop);
                    if (fr != null) created.Add(fr.Id);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[ClosedRegion] 创建填充区域失败: {ex.Message}");
                }
            }
            return created;
        }

        private static FilledRegion TryCreateFilledRegion(
            Document doc, ElementId typeId, ElementId viewId, CurveLoop loop)
        {
            try
            {
                return FilledRegion.Create(doc, typeId, viewId,
                    new List<CurveLoop> { loop });
            }
            catch { }
            try
            {
                loop.Flip();
                return FilledRegion.Create(doc, typeId, viewId,
                    new List<CurveLoop> { loop });
            }
            catch { return null; }
        }

        // ═══════════════════════════════════════════════════════
        //  获取或创建 FilledRegionType
        // ═══════════════════════════════════════════════════════
        public static ElementId GetOrCreateFilledRegionType(
            Document doc, string typeName, byte r, byte g, byte b)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault(t => t.Name == typeName);

            if (existing != null)
            {
                SetRegionTypeAppearance(existing, doc, r, g, b);
                return existing.Id;
            }

            var baseType = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException("文档中没有任何 FilledRegionType，无法创建。");

            var newType = baseType.Duplicate(typeName) as FilledRegionType;
            SetRegionTypeAppearance(newType, doc, r, g, b);
            return newType.Id;
        }

        private static void SetRegionTypeAppearance(
            FilledRegionType type, Document doc, byte r, byte g, byte b)
        {
            var color = new Color(r, g, b);
            var solid = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => { try { return fp.GetFillPattern().IsSolidFill; } catch { return false; } });

            if (solid != null) { type.ForegroundPatternId = solid.Id; type.ForegroundPatternColor = color; }
            else type.ForegroundPatternColor = color;

            try { type.BackgroundPatternId = ElementId.InvalidElementId; } catch { }
            try
            {
                var p = type.get_Parameter(BuiltInParameter.FILLED_REGION_MASKING);
                if (p != null && !p.IsReadOnly) p.Set(0);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════
        //  CurveLoop 有效性检查
        // ═══════════════════════════════════════════════════════
        private static bool IsValidLoop(CurveLoop loop)
        {
            if (loop == null) return false;
            var curves = loop.ToList();
            if (curves.Count < 2) return false;
            double tol = Constants.PointMergeTolerance * 2;
            for (int i = 0; i < curves.Count; i++)
            {
                XYZ end = curves[i].GetEndPoint(1);
                XYZ start = curves[(i + 1) % curves.Count].GetEndPoint(0);
                if (!end.IsAlmostEqualTo(start, tol)) return false;
            }
            return EstimateArea(loop) > 1e-6;
        }

        private static double EstimateArea(CurveLoop loop)
        {
            double area = 0;
            foreach (var c in loop)
            {
                double p0 = c.GetEndParameter(0), p1 = c.GetEndParameter(1);
                const int segs = 8;
                for (int i = 0; i < segs; i++)
                {
                    XYZ A = c.Evaluate(p0 + (p1 - p0) * i / segs, false);
                    XYZ B = c.Evaluate(p0 + (p1 - p0) * (i + 1) / segs, false);
                    area += A.X * B.Y - B.X * A.Y;
                }
            }
            return Math.Abs(area) / 2.0;
        }
    }
}