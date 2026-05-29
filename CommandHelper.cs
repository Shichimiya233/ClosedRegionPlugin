using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace ClosedRegionPlugin.Commands
{
    /// <summary>仅允许选择 CurveElement 的过滤器</summary>
    internal class CurveElementSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is CurveElement;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>两个命令共用的辅助方法</summary>
    internal static class CommandHelper
    {
        /// <summary>从用户选中的引用列表中提取有效 Curve 对象</summary>
        public static List<Curve> ExtractCurves(Document doc, IList<Reference> refs)
        {
            var result = new List<Curve>();
            foreach (var r in refs)
            {
                var el = doc.GetElement(r);
                if (el is CurveElement ce)
                {
                    var c = ce.GeometryCurve;
                    if (c != null && c.Length > 1e-6) result.Add(c);
                    continue;
                }
                try
                {
                    var geom = el?.get_Geometry(new Options());
                    if (geom == null) continue;
                    foreach (GeometryObject obj in geom)
                        if (obj is Curve curve && curve.Length > 1e-6)
                            result.Add(curve);
                }
                catch { }
            }
            return result;
        }
    }
}