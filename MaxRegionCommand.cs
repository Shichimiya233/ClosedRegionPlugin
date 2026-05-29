using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ClosedRegionPlugin.Core;

namespace ClosedRegionPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MaxRegionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;

            if (!(view is ViewPlan))
            {
                message = "请在楼层平面视图中运行此命令。";
                return Result.Failed;
            }

            IList<Reference> refs;
            try
            {
                refs = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new CurveElementSelectionFilter(),
                    "请选择所有边界曲线，完成后按 Finish");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            if (refs == null || refs.Count == 0)
            {
                TaskDialog.Show("提示", "未选择任何元素。");
                return Result.Cancelled;
            }

            var inputCurves = CommandHelper.ExtractCurves(doc, refs);
            if (inputCurves.Count == 0)
            {
                TaskDialog.Show("错误", "所选元素中未找到有效曲线。");
                return Result.Failed;
            }

            // 事务1：删除上次结果（独立提交）
            using (var txDel = new Transaction(doc, "删除旧封闭区域"))
            {
                txDel.Start();
                try
                {
                    FilledRegionHelper.DeleteExistingRegions(
                        doc, view.Id, Constants.MaxRegionTypeName);
                    FilledRegionHelper.DeleteExistingRegions(
                        doc, view.Id, Constants.MinRegionTypeName);
                    txDel.Commit();
                }
                catch
                {
                    if (txDel.GetStatus() == TransactionStatus.Started)
                        txDel.RollBack();
                }
            }

            // 预处理（事务外，纯计算）
            var processed = CurvePreprocessor.Process(inputCurves);
            var loops = RegionFinder.FindMaxRegions(processed);

            if (loops.Count == 0)
            {
                TaskDialog.Show("提示", "未找到有效的封闭区域。\n请检查曲线是否构成封闭图形。");
                return Result.Succeeded;
            }

            // 事务2：创建新结果
            using (var txCreate = new Transaction(doc, "生成最大封闭区域"))
            {
                txCreate.Start();
                try
                {
                    var col = Constants.YellowRGB;
                    var created = FilledRegionHelper.CreateFilledRegions(
                        doc, view.Id, loops, Constants.MaxRegionTypeName,
                        col[0], col[1], col[2]);
                    txCreate.Commit();
                    TaskDialog.Show("完成",
                        $"已生成 {created.Count} 个最大封闭区域（黄色）。\n" +
                        $"（输入 {inputCurves.Count} 条，预处理后 {processed.Count} 条，" +
                        $"找到 {loops.Count} 个轮廓）");
                }
                catch (Exception ex)
                {
                    if (txCreate.GetStatus() == TransactionStatus.Started)
                        txCreate.RollBack();
                    message = ex.Message;
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }
    }
}