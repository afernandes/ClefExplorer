using Microsoft.AspNetCore.Components;

namespace ClefExplorer.Components;

public partial class LogColumnOption
{
    [Parameter] public string Campo { get; set; } = "";
    [Parameter] public string Titulo { get; set; } = "";
    [Parameter] public bool Marcada { get; set; }
    [Parameter] public EventCallback<bool> OnToggle { get; set; }
}
