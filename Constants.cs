namespace ClosedRegionPlugin.Core
{
    /// <summary>全局常量与容差配置</summary>
    public static class Constants
    {
        // ── 容差控制（Revit 内部单位：英尺） ──────────────────
        // 需求说明：误差数据小于 5（绘图单位mm），换算：5mm≈0.0164ft
        // 此处设为 0.05ft（≈15mm）以覆盖手动绘图误差
        public const double PointMergeTolerance = 0.05;   // 点合并容差（ft）
        public const double TJunctionTolerance = 0.06;   // T形接头投影距离阈值（ft）

        // ── 填充区域类型名称 ──────────────────────────────────
        public const string MaxRegionTypeName = "最大区域";
        public const string MinRegionTypeName = "最小区域";

        // ── 颜色 RGB ──────────────────────────────────────────
        public static readonly byte[] YellowRGB = { 255, 255, 0 }; // 最大区域：黄色
        public static readonly byte[] MagentaRGB = { 255, 0, 255 }; // 最小区域：洋红
    }
}