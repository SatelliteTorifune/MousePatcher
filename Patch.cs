using Brutal.GlfwApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KSAModding
{
    
    internal enum EditorOrbitDragMode
    {
        None,
        Rotate, // LMB 拖动 = 旋转视角
        Pan     // RMB 拖动 = 平移视角
    }

    internal static class EditorOrbitState
    {
        public static EditorOrbitDragMode DragMode;
    }

    /// <summary>
    /// OrbitController（CameraMode.Orbit，默认相机控制器）。
    /// - 建造界面（Program.Editor != null）：LMB=旋转视角、RMB=平移视角、MMB=缩放拖动（原逻辑）。
    /// - 飞行模式：用 LMB 替换 RMB 拖动视角（原有逻辑）。
    /// 真实实现位于 OrbitController（CameraMode.Orbit）：
    /// 按下 -> IsPotentiallyDragging，移动超过阈值 -> IsDragging -> OnCursorPos 旋转/平移。
    /// 事件分发顺序（Program.OnMouseButton）：VehicleEditor → Vehicle → OrbitController，
    /// 因此本 Prefix 只在 VehicleEditor 未消费事件时被调用。
    /// </summary>
    [HarmonyPatch(typeof(OrbitController), nameof(OrbitController.OnMouseButton))]
    public static class MousePatcher_OrbitController
    {
        private static bool Prefix(
            OrbitController __instance,
            GlfwWindow window,
            GlfwMouseButton button,
            GlfwButtonAction action,
            GlfwModifier mods,
            ref bool __result)
        {
            // ---------- 建造界面：接管相机键位 ----------
            if (Program.Editor != null)
            {
                EditorOnMouseButton(__instance, button, action, ref __result);
                return false;
            }

            // ---------- 飞行模式：LMB 接管视角拖动 ----------
            // LMB 按下只置拖动状态、不消费事件（返回 false），
            // 以便双击聚焦星球（IOrbiter.OnMouseButtonOrbit）等原 LMB 点击逻辑仍可工作。
            if (button == GlfwMouseButton.Left)
            {
                if (action == GlfwButtonAction.Press)
                {
                    __instance.IsPotentiallyDragging = true;
                    __instance.CursorPositionScreenStartDrag = __instance.CursorPositionScreen;
                    __result = false;
                    return false;
                }

                if (action == GlfwButtonAction.Release)
                {
                    __instance.IsDragging = false;
                    __instance.IsPotentiallyDragging = false;
                    __result = false;
                    return false;
                }

                __result = false;
                return false;
            }

            // RMB(Right) 在飞行模式不再触发视角拖动
            if (button == GlfwMouseButton.Right)
            {
                __result = false;
                return false;
            }

            // 其余按钮（如中键）交回原方法
            return true;
        }

        /// <summary>
        /// 建造界面的鼠标按钮处理。
        /// LMB = 旋转、RMB = 平移、MMB = 缩放拖动（保留原逻辑）。
        /// </summary>
        private static void EditorOnMouseButton(
            OrbitController oc,
            GlfwMouseButton button,
            GlfwButtonAction action,
            ref bool __result)
        {
            // Prefix 已保证 Program.Editor != null，这里再加一道保护以消除空引用警告
            var editor = Program.Editor;
            if (editor == null)
            {
                __result = false;
                return;
            }

            if (action == GlfwButtonAction.Press)
            {
                switch (button)
                {
                    case GlfwMouseButton.Left:
                        // LMB = 旋转视角。仅在空白处（无高亮零件/gizmo）进入旋转预备，
                        // 避免与零件选中/抓取冲突（点击零件时 VehicleEditor 会处理）。
                        if (editor.Highlighted == null && editor.HighlightedGizmo == null)
                        {
                            oc.IsPotentiallyDragging = true;
                            oc.CursorPositionScreenStartDrag = oc.CursorPositionScreen;
                            EditorOrbitState.DragMode = EditorOrbitDragMode.Rotate;
                        }
                        __result = false;   // 放行：保留 VehicleEditor 的 LMB 交互
                        return;

                    case GlfwMouseButton.Right:
                        // RMB = 平移视角
                        oc.IsPotentiallyDragging = true;
                        oc.CursorPositionScreenStartDrag = oc.CursorPositionScreen;
                        EditorOrbitState.DragMode = EditorOrbitDragMode.Pan;
                        __result = false;
                        return;

                    case GlfwMouseButton.Middle:
                        // MMB = 缩放拖动（原逻辑）
                        oc.IsZoomDragging = true;
                        __result = true;
                        return;
                }

                __result = false;
                return;
            }

            if (action == GlfwButtonAction.Release)
            {
                switch (button)
                {
                    case GlfwMouseButton.Left:
                    case GlfwMouseButton.Right:
                        oc.IsDragging = false;
                        oc.IsPotentiallyDragging = false;
                        EditorOrbitState.DragMode = EditorOrbitDragMode.None;
                        __result = false;
                        return;

                    case GlfwMouseButton.Middle:
                        oc.IsZoomDragging = false;
                        __result = true;
                        return;
                }

                __result = false;
                return;
            }

            __result = false;
        }
    }

    /// <summary>
    /// 建造界面：LMB 拖动 = 旋转视角，RMB 拖动 = 平移视角，MMB 拖动 = 缩放（保留原逻辑）。
    /// 仅 Program.Editor != null（建造界面）时生效。
    /// </summary>
    [HarmonyPatch(typeof(OrbitController), nameof(OrbitController.OnCursorPos))]
    public static class MousePatcher_OrbitController_CursorPos
    {
        // 用于绕过 OrbitController.OnFrame 对 CameraOffset 的平滑插值动画（见 PanEditorCamera）
        private static readonly System.Reflection.FieldInfo _lastOffsetEditorField =
            AccessTools.Field(typeof(OrbitController), "_lastOffsetEditor");
        private static readonly System.Reflection.FieldInfo _lastOffsetEditorFinalField =
            AccessTools.Field(typeof(OrbitController), "_lastOffsetEditorFinal");

        private static bool Prefix(OrbitController __instance, GlfwWindow window, double2 pos, ref bool __result)
        {
            if (Program.Editor == null)
                return true;

            float2 newScreen = new float2((float)pos.X, (float)pos.Y);
            float2 delta = newScreen - __instance.CursorPositionScreen;
            __instance.CursorPositionScreen = newScreen;

            // MMB 缩放拖动（保留原逻辑）
            if (__instance.IsZoomDragging)
            {
                var following = __instance.Camera.Following;
                var orbitView = following?.OrbitView;
                if (orbitView != null)
                {
                    orbitView.DistancePower *= Math.Pow(1.1, (double)delta.Y * 0.1);
                    orbitView.DistancePower = Math.Clamp(orbitView.DistancePower, 5.0, 100.0);
                    // 去掉平滑：同步本控制器 DistancePower，使 OnFrame 的 Lerp 立即到位
                    __instance.DistancePower = orbitView.DistancePower;
                }
                __result = true;
                return false;
            }

            // 拖动阈值检测（移动超过 2px 才视为拖拽）
            if (__instance.IsPotentiallyDragging &&
                (__instance.CursorPositionScreen - __instance.CursorPositionScreenStartDrag).LengthSquared() > 2.0)
            {
                __instance.IsDragging = true;
                __instance.IsPotentiallyDragging = false;
            }

            if (__instance.IsDragging)
            {
                var following = __instance.Camera.Following;
                var orbitView = following?.OrbitView;
                if (orbitView != null)
                {
                    if (EditorOrbitState.DragMode == EditorOrbitDragMode.Pan)
                    {
                        // RMB：屏幕空间平移
                        PanEditorCamera(__instance, delta);
                    }
                    else
                    {
                        // LMB：旋转视角（默认）
                        orbitView.Azimuth -= (double)delta.X * 0.003;
                        orbitView.Elevation -= (double)delta.Y * 0.003;
                        orbitView.Elevation = Math.Clamp(orbitView.Elevation, -1.5707963267948966, 1.5707963267948966);
                    }
                }
                __result = true;
                return false;
            }

            __result = false;
            return false;
        }

        /// <summary>
        /// 屏幕空间平移：按屏幕像素位移直接移动 Editor.CameraOffset（Ecl 坐标）。
        /// 屏幕坐标 X 向右、Y 向下；期望场景跟随鼠标拖动方向：
        /// 鼠标右移(dx&gt;0) → 场景右移（相机左移），下移(dy&gt;0) → 场景下移（相机上移）。
        /// 相机距离 = DistancePower * MeanRadius（建造界面聚焦 VehicleEditingSpace，MeanRadius = 1）。
        /// </summary>
        private static void PanEditorCamera(OrbitController oc, float2 delta)
        {
            var editor = Program.Editor;
            var following = oc.Camera.Following;
            if (editor == null || following == null)
                return;

            double distance = oc.DistancePower * following.MeanRadius;

            double3 rightEcl = oc.Camera.GetRight();
            double3 upEcl = oc.Camera.GetUp();

            // 右移(dx&gt;0)→沿 -right 移动相机（场景右移）；下移(dy&gt;0)→沿 +up 移动相机（场景下移）
            double3 worldDelta = rightEcl * (-(double)delta.X) + upEcl * (double)delta.Y;

            // 每像素世界位移 ≈ 距离 * tan(半FOV) * 2 / 视口高度（tan(半FOV) 取约 0.75）
            double viewportHeight = oc.Camera.FramebufferSize.Y;
            if (viewportHeight <= 0.0)
                return;
            double panScale = 1.5 * distance / viewportHeight;

            editor.CameraOffset += worldDelta * panScale;
            
            _lastOffsetEditorField.SetValue(oc, editor.CameraOffset);
            _lastOffsetEditorFinalField.SetValue(oc, editor.CameraOffset);
        }
    }

    /// <summary>
    /// 建造界面：滚轮直接缩放视角。
    /// 去掉原版"无 Shift 滚轮 = 沿 X 轴上下移动镜头"（Editor.CameraOffset.X 平移），
    /// 也不再把 Shift 作为缩放的必要条件。
    /// </summary>
    [HarmonyPatch(typeof(OrbitController), nameof(OrbitController.OnScroll))]
    public static class MousePatcher_OrbitController_Scroll
    {
        private static bool Prefix(OrbitController __instance, GlfwWindow window, double2 offset, ref bool __result)
        {
            if (Program.Editor == null)
                return true;

            // MMB 缩放拖动中，滚轮忽略
            if (__instance.IsZoomDragging)
            {
                __result = true;
                return false;
            }

            var following = __instance.Camera.Following;
            var orbitView = following?.OrbitView;
            if (orbitView == null)
            {
                __result = false;
                return false;
            }

            // 滚轮缩放（Min/Max 与原版 EditorOnScroll 一致：5 ~ 100）
            if (offset.Y > 0.0)
                orbitView.DistancePower /= 1.1;
            else
                orbitView.DistancePower *= 1.1;
            orbitView.DistancePower = Math.Clamp(orbitView.DistancePower, 5.0, 100.0);

            // 去掉平滑：同步本控制器 DistancePower，使 OnFrame 的 Lerp 立即到位
            __instance.DistancePower = orbitView.DistancePower;

            __result = true;
            return false;
        }
    }

    /// <summary>
    /// 建造界面：解决 LMB 旋转与零件交互的事件冲突。
    /// 事件分发顺序为 VehicleEditor 优先；当 LMB 正在旋转拖拽（OrbitController.IsDragging）时，
    /// 松手应跳过零件选中/抓取并放行给 OrbitController，否则旋转状态会悬挂、视角停不下来。
    /// 同时清除非拖拽松手时悬挂的拖动预备状态，避免点击后移动鼠标误触发旋转/平移。
    /// </summary>
    [HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnMouseButton))]
    public static class MousePatcher_VehicleEditor
    {
        private static bool Prefix(
            VehicleEditor __instance,
            GlfwWindow window,
            GlfwMouseButton button,
            GlfwButtonAction action,
            GlfwModifier mods,
            ref bool __result)
        {
            if (action != GlfwButtonAction.Release)
                return true;

            if (Program.HoveredViewport?.GetActiveController() is OrbitController oc)
            {
                // LMB 正在旋转拖拽：跳过零件交互，放行给 OrbitController 清除拖动状态
                //（RMB 平移拖拽时无需处理——VehicleEditor 原方法已有 IsMouseDrag 放行逻辑）
                if (button == GlfwMouseButton.Left && oc.IsDragging)
                {
                    __result = false;
                    return false;
                }

                // 未拖拽的 LMB/RMB 松开：清除悬挂的拖动预备状态，
                // 防止点击后移动鼠标误触发旋转/平移（RMB 拖拽中不能清，否则右键菜单会被误弹）
                if (!oc.IsDragging && (button == GlfwMouseButton.Left || button == GlfwMouseButton.Right))
                {
                    oc.CancelMouseDrag();
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 地图视图（CameraMode.Map）：LMB=旋转视角、RMB=平移视角、MMB 不再平移。
    /// 原逻辑：RMB=旋转视角，MMB（或 Shift+MMB）=平移视角。
    /// 注意：LMB 按下只置旋转拖动状态、不消费事件（返回 false），
    /// 以便双击聚焦星球（IOrbiter.OnMouseButtonOrbit）等原 LMB 点击逻辑仍可工作。
    /// </summary>
    [HarmonyPatch(typeof(MapController), nameof(MapController.OnMouseButton))]
    public static class MousePatcher_MapController
    {
        private static bool Prefix(
            MapController __instance,
            GlfwWindow window,
            GlfwMouseButton button,
            GlfwButtonAction action,
            GlfwModifier mods,
            ref bool __result)
        {
            if (action == GlfwButtonAction.Press)
            {
                // LMB = 旋转视角（与飞行一致）；不消费，保留双击聚焦等原 LMB 点击逻辑
                if (button == GlfwMouseButton.Left)
                {
                    __instance.RotateMouseDragging = true;
                    __instance.TranslateMouseDragging = false;
                    __result = false;
                    return false;
                }

                // RMB = 平移视角（取代原 MMB 平移）
                if (button == GlfwMouseButton.Right)
                {
                    __instance.TranslateMouseDragging = true;
                    __instance.RotateMouseDragging = false;
                    __result = true;
                    return false;
                }

                // MMB：不再平移视角，消费事件但不做任何操作
                if (button == GlfwMouseButton.Middle)
                {
                    __result = true;
                    return false;
                }

                __result = false;
                return false;
            }

            if (action == GlfwButtonAction.Release)
            {
                if (button == GlfwMouseButton.Left)
                {
                    __instance.RotateMouseDragging = false;
                    __result = false;
                    return false;
                }

                if (button == GlfwMouseButton.Right)
                {
                    __instance.TranslateMouseDragging = false;
                    __result = true;
                    return false;
                }

                if (button == GlfwMouseButton.Middle)
                {
                    __result = true;
                    return false;
                }

                __result = false;
                return false;
            }

            __result = false;
            return false;
        }
    }

    /// <summary>
    /// 飞行界面：把"RMB 点击选择零件"改为"LMB 点击选择零件"。
    /// 原逻辑（Vehicle.OnMouseButton）：RMB Release 且未拖拽、高亮零件非空时置 PartClicked=true 并消费。
    /// 现改为 LMB Release 触发选择；RMB 不再选择。
    /// 与 LMB 相机拖动（MousePatcher_OrbitController 飞行分支）协调：
    /// 拖拽中（IsMouseDrag）松手放行给相机、不选择零件；点击（未拖拽）才选择。
    /// </summary>
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnMouseButton))]
    public static class MousePatcher_Vehicle
    {
        private static bool Prefix(
            Vehicle __instance,
            GlfwWindow window,
            GlfwMouseButton button,
            GlfwButtonAction action,
            GlfwModifier mods,
            ref bool __result)
        {
            // 建造界面不干预
            if (Program.Editor != null)
                return true;

            // LMB Release：选择零件（原为 RMB）
            if (action == GlfwButtonAction.Release && button == GlfwMouseButton.Left)
            {
                var controller = Program.HoveredViewport.GetActiveController();

                // 相机拖拽中：放行给 OrbitController 清除拖动，不选择零件
                if (controller.IsMouseDrag())
                {
                    __result = false;
                    return false;
                }

                // 未拖拽（点击）：清除悬挂的拖动预备，避免之后移动鼠标误旋转
                controller.CancelMouseDrag();

                if (__instance.Highlighted != null)
                {
                    __instance.Highlighted.PartClicked = true;
                    __result = true;      // 消费事件
                    return false;         // 跳过原方法（原 LMB 走 BurnPlan）
                }

                // 空白：交回原方法（保持 BurnPlan 处理与放行给 OrbitController）
                return true;
            }

            // RMB Release：不再触发"选择零件"
            if (action == GlfwButtonAction.Release && button == GlfwMouseButton.Right)
            {
                var controller = Program.HoveredViewport.GetActiveController();
                if (controller.IsMouseDrag())
                {
                    __result = false;
                    return false;
                }

                controller.CancelMouseDrag();
                if (__instance.Highlighted != null)
                {
                    // 阻止原 RMB 选择；不消费，放行给后续
                    __result = false;
                    return false;
                }

                // 空白：交回原方法（BurnPlan 等）
                return true;
            }

            return true;
        }
    }
}
