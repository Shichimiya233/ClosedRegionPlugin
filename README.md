# ClosedRegionPlugin

基于 Revit API 的封闭区域检测插件，自动识别楼层平面视图中详图线所围成的封闭区域，并以填充区域的形式输出结果。

---

## 功能介绍

插件提供两个命令：

| 命令 | 功能 | 输出颜色 |
|------|------|----------|
| **MaxRegionCommand** | 生成最大封闭区域（每个连通分量的外轮廓） | 🟡 黄色 |
| **MinRegionCommand** | 生成 N 个最小封闭区域（所有原子格子） | 🟣 洋红色 |

每次点击命令时，会自动清空上一次生成的结果，再重新生成。

---

## 环境要求

- Autodesk Revit 2022 或 Revit 2026
- .NET Framework 4.8
- Windows 10 / 11

---

## 安装方法

1. 下载本仓库，编译生成 `ClosedRegionPlugin.dll`
2. 将以下两个文件复制到 Revit 插件目录：

```
C:\ProgramData\Autodesk\Revit\Addins\2022\
├── ClosedRegionPlugin.dll
└── ClosedRegionPlugin.addin
```

3. 启动 Revit，插件会自动加载

---

## 使用方法

1. 打开 Revit，进入**楼层平面视图**
2. 在功能区找到插件命令（**MaxRegionCommand** 或 **MinRegionCommand**）
3. 框选所有白色详图线
4. 按 **Finish** 完成选择
5. 程序自动生成填充区域

---

## 项目结构

```
ClosedRegionPlugin/
├── Core/
│   ├── Constants.cs            # 全局容差常量与配置
│   ├── CurvePreprocessor.cs    # 曲线预处理（相交检测、打断、合并、去重）
│   ├── PlanarGraph.cs          # DCEL 平面图数据结构
│   ├── RegionFinder.cs         # 封闭区域查找（最大 / 最小）
│   └── FilledRegionHelper.cs   # Revit 填充区域管理
└── Commands/
    ├── CommandHelper.cs        # 共用辅助方法
    ├── MaxRegionCommand.cs     # 最大区域命令入口
    └── MinRegionCommand.cs     # 最小区域命令入口
```

---

## 核心算法

### 曲线预处理
- 完全不依赖 Revit `Intersect` API，改用**纯 2D 解析几何**：
  - 直线 × 直线：行列式法
  - 直线 × 弧线：二次方程法
- 支持共线重叠检测与去重
- 支持 T 形 / 十字接头自动打断
- 端点容差合并（默认 0.05ft ≈ 15mm，可在 `Constants.cs` 调整）

### 平面图构建（DCEL）
- 自定义实现双向链表边结构（Vertex / HalfEdge / Face）
- 出边按 CCW 角度排序，通过最右转规则链接 Next 指针
- Shoelace 公式计算有向面积，区分内部有界面与外部无界面

### 区域查找
- **最小区域**：直接枚举平面图中所有有界面
- **最大区域**：BFS 分连通分量 → 提取边界半边 → 串联外轮廓

---

## 处理的特殊情况

| 情况 | 处理方式 |
|------|----------|
| 直线、圆弧、样条曲线混合输入 | 按类型分派到不同求交方法 |
| 曲线相互交叉 | 解析几何求交点，在交点处打断 |
| 共线重叠区段 | 端点互投影，打断后去重 |
| 孤岛区域（互不连接的封闭区域） | BFS 连通分量分别处理，各自生成最大区域 |
| 浮点误差导致的微小间隙 | 容差合并（< 5mm 自动视为连接） |
| T 形 / 十字接头 | 端点投影检测，强制打断长边 |

---

## 开发说明

- 禁止使用 Revit 原生房间功能（Room / RoomSeparationLine）
- 所有区域检测基于详图线几何 + 自定义 DCEL 数据结构实现
- 填充区域类型名称：`最大区域`（黄色）/ `最小区域`（洋红色）# ClosedRegionPlugin
