using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using STS2PGN.Mod.Recording;
using STS2PGN.Scripting;

namespace STS2PGN.Mod.Playback;

/// <summary>Controls only the playback driver; automatic game effects keep running.</summary>
internal sealed class PlaybackStepOverlay : IDisposable
{
    private static PlaybackStepOverlay? _active;
    private static readonly StringName[] GameActions = typeof(MegaInput)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(StringName))
        .Select(field => (StringName)field.GetValue(null)!).ToArray();
    private static readonly FieldInfo? DebugBindings = typeof(NInputManager)
        .GetField("_debugInputMap", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly PlaybackStepGate _gate;
    private readonly CanvasLayer _layer = new() { Layer = 100 };
    private readonly Label _status = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly string[] _actions;
    private readonly Label[] _actionRows = new Label[PlaybackStepContext.Radius * 2 + 1];
    private readonly Button _button = new() { FocusMode = Control.FocusModeEnum.None };
    private readonly Label _shortcutStatus = new()
    {
        MouseFilter = Control.MouseFilterEnum.Ignore,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };
    private readonly SceneTree _tree;
    private readonly NInputManager? _inputManager;
    private readonly Key _key;

    public PlaybackStepOverlay(PlaybackStepGate gate, Key key, IReadOnlyList<ScriptDecision> decisions)
    {
        _gate = gate;
        _key = key;
        _actions = decisions.Select(decision => PlaybackStepContext.Describe(decision.Kind, decision.Data)).ToArray();
        NGame game = NGame.Instance ?? throw new InvalidOperationException("Game UI is unavailable.");
        _tree = game.GetTree();
        _inputManager = NInputManager.Instance;
        PanelContainer panel = new() { CustomMinimumSize = new Vector2(420, 0) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.72f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
        });
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        panel.OffsetLeft = -440;
        panel.OffsetRight = -20;
        panel.OffsetTop = 90;
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        MarginContainer margin = new();
        foreach (string side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 12);
        }
        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 8);
        column.AddChild(_status);
        Label context = new()
        {
            Text = "Previous 5 / Current / Next 5",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        context.AddThemeFontSizeOverride("font_size", 16);
        column.AddChild(context);
        VBoxContainer actions = new();
        actions.AddThemeConstantOverride("separation", 2);
        for (int index = 0; index < _actionRows.Length; index++)
        {
            Label row = new()
            {
                MouseFilter = Control.MouseFilterEnum.Pass,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            row.AddThemeFontSizeOverride("font_size", 16);
            _actionRows[index] = row;
            actions.AddChild(row);
        }
        column.AddChild(actions);
        column.AddChild(_button);
        column.AddChild(_shortcutStatus);
        margin.AddChild(column);
        panel.AddChild(margin);
        _layer.AddChild(panel);
        game.AddChild(_layer);
        _button.Pressed += OnNextPressed;
        _gate.Changed += Refresh;
        _tree.ProcessFrame += UpdateAvailability;
        if (_inputManager is not null)
        {
            _inputManager.InputRebound += Refresh;
        }
        _active = this;
        Refresh();
    }

    private bool InputBlocked => !NGame.IsGameFocusedWindow()
        || NDevConsole.Instance.Visible
        || _layer.GetViewport().GuiGetFocusOwner() is { } focus
            && focus is LineEdit or TextEdit && focus.IsVisibleInTree();

    private string? ShortcutConflict()
    {
        if (_key == Key.None)
        {
            return "Keyboard shortcut disabled";
        }

        if (NInputManager.Instance is { } manager)
        {
            if (GameActions.Any(action => manager.GetShortcutKey(action) == _key)
                || DebugBindings?.GetValue(manager) is IReadOnlyDictionary<Key, StringName> debug && debug.ContainsKey(_key))
            {
                return $"{_key} is a game shortcut; use the button";
            }
        }

        // These direct handlers do not appear in the configurable shortcut table.
        if (_key is Key.F2 or Key.F11 or Key.Quoteleft or Key.Apostrophe or Key.Asterisk or Key.Asciicircum)
        {
            return $"{_key} is a game shortcut; use the button";
        }
        foreach (StringName action in InputMap.GetActions())
        {
            if (InputMap.ActionGetEvents(action).OfType<InputEventKey>()
                .Any(input => input.Keycode == _key || input.PhysicalKeycode == _key))
            {
                return $"{_key} is already bound; use the button";
            }
        }
        return null;
    }

    private void Refresh()
    {
        string? conflict = ShortcutConflict();
        _status.Text = _gate.Position == 0 ? "Preparing playback"
            : $"{(_gate.IsWaiting ? "Paused" : "Running")} · Step {_gate.Position} of {_gate.Total}";
        IReadOnlyList<PlaybackActionRow> rows = PlaybackStepContext.Window(_actions, _gate.Position);
        for (int index = 0; index < _actionRows.Length; index++)
        {
            Label label = _actionRows[index];
            label.Visible = index < rows.Count;
            if (index >= rows.Count) continue;
            PlaybackActionRow row = rows[index];
            bool current = row.Position == _gate.Position;
            string marker = current ? (_gate.IsWaiting ? "Next ▶ " : "Now ▶ ") : "";
            label.Text = $"{marker}{row.Position}. {row.Description}";
            label.TooltipText = label.Text;
            label.AddThemeColorOverride("font_color", current ? new Color(1f, 0.85f, 0.4f)
                : row.Position < _gate.Position ? new Color(0.7f, 0.73f, 0.78f) : Colors.White);
        }
        _button.Text = conflict is null ? $"Next step · {_key}" : "Next step";
        UpdateAvailability();
        _shortcutStatus.Text = conflict ?? "One press advances one decision";
    }

    private void UpdateAvailability()
    {
        _button.Disabled = !_gate.IsWaiting || InputBlocked;
        if (_gate.IsWaiting && !RecorderService.IsRecording)
        {
            PlaybackService.Cancel();
        }
    }

    private void OnNextPressed()
    {
        if (!InputBlocked)
        {
            _gate.Advance();
        }
    }

    internal static bool HandleInput(InputEvent input)
    {
        if (_active is not { } overlay || overlay.InputBlocked
            || input is not InputEventKey key || key.Keycode != overlay._key
            || key.AltPressed || key.CtrlPressed || key.MetaPressed || key.ShiftPressed
            || overlay.ShortcutConflict() is not null)
        {
            return false;
        }

        if (key.Pressed && !key.Echo)
        {
            overlay._gate.Advance();
        }
        overlay._layer.GetViewport().SetInputAsHandled();
        return true;
    }

    public void Dispose()
    {
        _active = null;
        _tree.ProcessFrame -= UpdateAvailability;
        if (_inputManager is not null)
        {
            _inputManager.InputRebound -= Refresh;
        }
        _gate.Changed -= Refresh;
        _button.Pressed -= OnNextPressed;
        _layer.QueueFree();
    }
}

[HarmonyPatch(typeof(NInputManager), nameof(NInputManager._UnhandledKeyInput))]
internal static class PlaybackStepInputPatch
{
    [HarmonyPrefix]
    private static bool Prefix(InputEvent inputEvent)
    {
        bool handled = false;
        RecorderService.ExecuteSafely("handling playback step key", () =>
            handled = PlaybackStepOverlay.HandleInput(inputEvent));
        return !handled;
    }
}
