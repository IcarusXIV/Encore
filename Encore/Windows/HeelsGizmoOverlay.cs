using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using CSMatrix = FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4;
using CSQuat = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using CameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace Encore.Windows;

public class HeelsGizmoTarget
{
    public float X;
    public float Y;
    public float Z;
    public float Rotation;
    public float Pitch;
    public float Roll;
}

// In-world ImGuizmo gizmo. Subscribes to UiBuilder.Draw to render outside any ImGui window.
// Ported from SimpleHeels UIGizmoOverlay.
public sealed unsafe class HeelsGizmoOverlay : IDisposable
{
    public static HeelsGizmoTarget? Target { get; set; }
    public static string? Label { get; set; }
    public static Action<HeelsGizmoTarget>? OnChanged { get; set; }

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;

    public HeelsGizmoOverlay(
        IDalamudPluginInterface pluginInterface,
        ICondition condition,
        IObjectTable objectTable,
        IGameGui gameGui)
    {
        this.pluginInterface = pluginInterface;
        this.condition = condition;
        this.objectTable = objectTable;
        this.gameGui = gameGui;

        this.pluginInterface.UiBuilder.Draw += OnDraw;
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= OnDraw;
        Target = null;
        Label = null;
        OnChanged = null;
    }

    private static CSMatrix _itemMatrix = CSMatrix.Identity;
    private static Vector3 _position;
    private static Vector3 _rotation;
    private static Vector3 _scale = new(1f);
    private static Stopwatch _unlockDelay = Stopwatch.StartNew();
    private static ImGuizmoOperation? _lockOperation;
    private static Vector2 _rotStart;
    private static Vector2 _rotCenter;
    private static bool _gizmoActiveLast;

    private static ImGuizmoOperation? LockOp
    {
        get => _lockOperation;
        set
        {
            if (value == null)
            {
                if (_unlockDelay.ElapsedMilliseconds < 250) return;
                _lockOperation = null;
            }
            else
            {
                _lockOperation = value;
                _unlockDelay.Restart();
            }
        }
    }

    private void OnDraw()
    {
        var target = Target;
        if (target == null) return;
        if (objectTable.LocalPlayer == null) return;
        if (condition.Any(ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.InCombat))
            return;

        var chara = (Character*)(objectTable.LocalPlayer?.Address ?? nint.Zero);
        if (chara == null || chara->GameObject.DrawObject == null) return;

        var activeCamera = CameraManager.Instance()->GetActiveCamera();
        if (activeCamera == null) return;

        _position = chara->GameObject.DrawObject->Position;
        var q = chara->GameObject.DrawObject->Rotation;
        _rotation = new Vector3(0, q.EulerAngles.Y, 0);

        if (!ImGuizmo.IsUsing())
            ImGuizmo.RecomposeMatrixFromComponents(ref _position.X, ref _rotation.X, ref _scale.X, ref _itemMatrix.M11);

        if (!string.IsNullOrWhiteSpace(Label) && gameGui.WorldToScreen(_position, out var scr))
        {
            var fdl = ImGui.GetForegroundDrawList();
            var textSize = ImGui.CalcTextSize(Label);
            var pos = new Vector2(scr.X - textSize.X / 2f, scr.Y - 40f);
            fdl.AddRectFilled(pos - new Vector2(6, 4), pos + textSize + new Vector2(6, 4),
                ImGui.GetColorU32(new Vector4(0, 0, 0, 0.6f)), 4f);
            fdl.AddText(pos, ImGui.GetColorU32(new Vector4(0.95f, 0.75f, 0.45f, 1f)), Label);
        }

        var modified = false;

        try
        {
            var cam = activeCamera->SceneCamera.RenderCamera;
            var view = activeCamera->SceneCamera.ViewMatrix;
            var proj = cam->ProjectionMatrix;
            var far = cam->FarPlane;
            var near = cam->NearPlane;
            var clip = far / (far - near);
            proj.M43 = -(clip * near);
            proj.M33 = -((far + near) / (far - near));
            view.M44 = 1.0f;

            if ((LockOp is null or ImGuizmoOperation.TranslateZ) &&
                DrawHandle(ref view, ref proj, ImGuizmoMode.Local, ImGuizmoOperation.TranslateZ))
            {
                LockOp = ImGuizmoOperation.TranslateZ;
                var lp = WorldToLocal(_itemMatrix.Translation, _position,
                    CSQuat.CreateFromYawPitchRoll(chara->GameObject.Rotation + target.Rotation, 0, 0));
                target.Z += lp.Z;
                modified = true;
            }
            if ((LockOp is null or ImGuizmoOperation.TranslateX) &&
                DrawHandle(ref view, ref proj, ImGuizmoMode.Local, ImGuizmoOperation.TranslateX))
            {
                LockOp = ImGuizmoOperation.TranslateX;
                var lp = WorldToLocal(_itemMatrix.Translation, _position,
                    CSQuat.CreateFromYawPitchRoll(chara->GameObject.Rotation + target.Rotation, 0, 0));
                target.X += lp.X;
                modified = true;
            }
            if ((LockOp is null or ImGuizmoOperation.TranslateY) &&
                DrawHandle(ref view, ref proj, ImGuizmoMode.World, ImGuizmoOperation.TranslateY))
            {
                LockOp = ImGuizmoOperation.TranslateY;
                target.Y += _itemMatrix.Translation.Y - _position.Y;
                modified = true;
            }
            if ((LockOp is null or ImGuizmoOperation.RotateY) &&
                DrawHandle(ref view, ref proj, ImGuizmoMode.Local, ImGuizmoOperation.RotateY))
            {
                if (LockOp != ImGuizmoOperation.RotateY)
                {
                    _rotStart = ImGui.GetMousePos();
                    if (gameGui.WorldToScreen(_position, out var c))
                    {
                        _rotCenter = c;
                        LockOp = ImGuizmoOperation.RotateY;
                    }
                    else
                    {
                        LockOp = ImGuizmoOperation.Bounds;
                    }
                }
                else
                {
                    var mouse = ImGui.GetMousePos();
                    var a = MathF.Atan2(mouse.Y - _rotCenter.Y, mouse.X - _rotCenter.X)
                          - MathF.Atan2(_rotStart.Y - _rotCenter.Y, _rotStart.X - _rotCenter.X);
                    if (MathF.Abs(a) > 0.0001f)
                    {
                        var prev = Rotate2D(new Vector2(target.X, target.Z), -target.Rotation);
                        target.Rotation -= a;
                        if (target.Rotation > MathF.Tau) target.Rotation -= MathF.Tau;
                        if (target.Rotation < -MathF.Tau) target.Rotation += MathF.Tau;
                        var next = Rotate2D(prev, target.Rotation);
                        target.X = next.X;
                        target.Z = next.Y;
                        _rotStart = mouse;
                        modified = true;
                    }
                }
            }

            if ((LockOp is null or ImGuizmoOperation.RotateX) &&
                DrawHandle(ref view, ref proj, ImGuizmoMode.Local, ImGuizmoOperation.RotateX))
            {
                if (LockOp != ImGuizmoOperation.RotateX)
                {
                    _rotStart = ImGui.GetMousePos();
                    if (gameGui.WorldToScreen(_position, out var c))
                    {
                        _rotCenter = c;
                        LockOp = ImGuizmoOperation.RotateX;
                    }
                    else
                    {
                        LockOp = ImGuizmoOperation.Bounds;
                    }
                }
                else
                {
                    var mouse = ImGui.GetMousePos();
                    var a = MathF.Atan2(mouse.Y - _rotCenter.Y, mouse.X - _rotCenter.X)
                          - MathF.Atan2(_rotStart.Y - _rotCenter.Y, _rotStart.X - _rotCenter.X);
                    if (MathF.Abs(a) > 0.0001f)
                    {
                        target.Pitch += a;
                        if (target.Pitch > MathF.Tau) target.Pitch -= MathF.Tau;
                        if (target.Pitch < -MathF.Tau) target.Pitch += MathF.Tau;
                        _rotStart = mouse;
                        modified = true;
                    }
                }
            }

            if ((LockOp is null or ImGuizmoOperation.RotateZ) &&
                DrawHandle(ref view, ref proj, ImGuizmoMode.Local, ImGuizmoOperation.RotateZ))
            {
                if (LockOp != ImGuizmoOperation.RotateZ)
                {
                    _rotStart = ImGui.GetMousePos();
                    if (gameGui.WorldToScreen(_position, out var c))
                    {
                        _rotCenter = c;
                        LockOp = ImGuizmoOperation.RotateZ;
                    }
                    else
                    {
                        LockOp = ImGuizmoOperation.Bounds;
                    }
                }
                else
                {
                    var mouse = ImGui.GetMousePos();
                    var a = MathF.Atan2(mouse.Y - _rotCenter.Y, mouse.X - _rotCenter.X)
                          - MathF.Atan2(_rotStart.Y - _rotCenter.Y, _rotStart.X - _rotCenter.X);
                    if (MathF.Abs(a) > 0.0001f)
                    {
                        target.Roll -= a;
                        if (target.Roll > MathF.Tau) target.Roll -= MathF.Tau;
                        if (target.Roll < -MathF.Tau) target.Roll += MathF.Tau;
                        _rotStart = mouse;
                        modified = true;
                    }
                }
            }
        }
        finally
        {
            ImGuizmo.SetID(-1);
        }

        if (!ImGuizmo.IsUsing() && !_gizmoActiveLast)
            LockOp = null;
        _gizmoActiveLast = ImGuizmo.IsUsing();

        if (modified)
            OnChanged?.Invoke(target);
    }

    private static bool DrawHandle(ref CSMatrix view, ref CSMatrix proj,
        ImGuizmoMode mode, ImGuizmoOperation op)
    {
        try
        {
            ImGuizmo.SetID((int)ImGui.GetID($"EncoreHeels#{op}"));
            var vp = ImGui.GetMainViewport();
            ImGuizmo.Enable(true);
            ImGuizmo.SetOrthographic(false);
            ImGuizmo.AllowAxisFlip(false);
            ImGuizmo.SetDrawlist(ImGui.GetBackgroundDrawList());
            ImGuizmo.SetRect(vp.Pos.X, vp.Pos.Y, vp.Size.X, vp.Size.Y);
            var delta = new CSMatrix();
            return ImGuizmo.Manipulate(ref view.M11, ref proj.M11, op, mode, ref _itemMatrix.M11, ref delta.M11);
        }
        finally
        {
            ImGuizmo.SetID(-1);
        }
    }

    private static Vector3 WorldToLocal(Vector3 worldPoint, Vector3 objPos, CSQuat objRot)
    {
        var translated = worldPoint - objPos;
        var inv = System.Numerics.Quaternion.Inverse(objRot);
        return Vector3.Transform(translated, inv);
    }

    private static Vector2 Rotate2D(Vector2 p, float angleRad)
    {
        var c = MathF.Cos(angleRad);
        var s = MathF.Sin(angleRad);
        return new Vector2(p.X * c - p.Y * s, p.X * s + p.Y * c);
    }
}
