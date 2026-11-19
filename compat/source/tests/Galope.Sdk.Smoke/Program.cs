using Galope;
using R36S.A11y;

Check(GamepadReader.EventRecordSizeForPointer(4) == 16 &&
    GamepadReader.EventRecordSizeForPointer(8) == 24, "tamanho do evdev ARMv7 e ARM64");
foreach (ushort fn in new ushort[] { InputState.FnCode, InputState.LegacyFnCode })
{
    var input = new InputState(.2);
    Check(input.Feed(1, fn, 1, 0) is null, "Fn reservado");
    Check(input.Feed(1, 304, 1, 1) is null, "B com Fn não chega ao jogo");
    Check(input.Feed(3, 0, 255, 2) is null, "analógico com Fn não chega ao jogo");
    Check(input.Feed(1, 304, 0, 3) is null, "soltar B reservado não vaza");
    input.Feed(1, fn, 0, 4);
    Check(input.Feed(1, 304, 1, 5) is ButtonEvent { Button: Button.B },
        "B volta após Fn");
}
var pad = new InputState(.2);
var fnAxis = new InputState(.2);
fnAxis.Feed(1, InputState.FnCode, 1, 0);
Check(fnAxis.Feed(3, 0, 1800, 1) is null, "analógico com Fn reservado");
fnAxis.Feed(1, InputState.FnCode, 0, 2);
Check(fnAxis.Feed(3, 0, 1700, 3) is null, "analógico continua reservado até o centro");
fnAxis.Feed(3, 0, 0, 4);
Check(fnAxis.Feed(3, 0, 1800, 5) is AxisEvent { Value: 1 }, "analógico retorna após centro");
Check(pad.Feed(1, 103, 1, 0) is ButtonEvent { Button: Button.Up } &&
    pad.Feed(1, 315, 1, 0) is ButtonEvent { Button: Button.Start },
    "códigos do controle GA36-MB");
Check(SdkControls.Map(new(Button.B, ButtonState.Pressed, 0), false) is
    InputEvent { Action: "confirm", Physical: "bottom" } &&
    SdkControls.Map(new(Button.B, ButtonState.Pressed, 0), true) is
    InputEvent { Action: "back", Physical: "bottom" } &&
    SdkControls.Map(new(Button.A, ButtonState.Pressed, 0), true) is
    InputEvent { Action: "confirm" }, "layout Xbox e Switch");
var axes = new Dictionary<Axis, int>();
Check(SdkControls.MapAxis(Axis.LY, -1, axes) == "up" &&
    SdkControls.MapAxis(Axis.LY, -.8, axes) is null &&
    SdkControls.MapAxis(Axis.LY, 0, axes) is null &&
    SdkControls.MapAxis(Axis.LY, 1, axes) == "down", "direção analógica por borda");
Console.WriteLine("Galope SDK: Fn, evdev, layout e analógicos OK");

static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
}

namespace Galope
{
    internal readonly record struct InputEvent(string Action, string Physical = "", bool? Graphics = null);
}
