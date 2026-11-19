using Galope;
using R36S.A11y;

internal static class SdkControls
{
    internal static InputEvent? Map(ButtonEvent e, bool switchLayout)
    {
        if (e.State != ButtonState.Pressed) return null;
        string? action = e.Button switch
        {
            Button.Up => "up", Button.Down => "down", Button.Left => "left",
            Button.Right => "right", Button.Start => "start",
            Button.B => switchLayout ? "back" : "confirm",
            Button.A => switchLayout ? "confirm" : "back",
            _ => null
        };
        return action is null ? null :
            new InputEvent(action, e.Button == Button.B ? "bottom" : "");
    }

    internal static string? MapAxis(Axis axis, double value, IDictionary<Axis, int> state)
    {
        if (axis is not (Axis.LX or Axis.LY)) return null;
        int direction = value < -.55 ? -1 : value > .55 ? 1 : 0;
        if (state.TryGetValue(axis, out int previous) && previous == direction) return null;
        state[axis] = direction;
        if (direction == 0) return null;
        return axis == Axis.LX ? (direction < 0 ? "left" : "right") :
            (direction < 0 ? "up" : "down");
    }
}
