namespace InputForge.Core;

public sealed class BindingSelection
{
    public Control Current { get; private set; } = Control.A;
    public event Action<Control>? Changed;

    public void Select(Control control)
    {
        if (Current == control) return;
        Current = control;
        Changed?.Invoke(control);
    }
}
