using System;
using System.Windows.Input;

namespace ArisenEditorFramework.Inspector;

public sealed class ActionPropertyItemViewModel : PropertyItemViewModel
{
    public ActionPropertyItemViewModel(
        string name,
        string buttonText,
        ICommand command,
        string category = "Actions",
        string description = "")
        : base(command, name, typeof(ICommand), false, category)
    {
        ButtonText = buttonText;
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Description = description;
    }

    public string ButtonText { get; }
    public ICommand Command { get; }

    public override object? Value
    {
        get => ButtonText;
        set { }
    }
}
