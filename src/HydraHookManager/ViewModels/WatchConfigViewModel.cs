using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HydraHookManager.Models;

namespace HydraHookManager.ViewModels;

public sealed partial class WatchConfigViewModel : ObservableObject
{
    public WatchConfigViewModel(string? target, WatchTrigger trigger, int delayMs, string dllSummary)
    {
        _target = target ?? "";
        _trigger = trigger;
        _delayText = delayMs.ToString(CultureInfo.InvariantCulture);
        DllSummary = dllSummary;
    }

    public IReadOnlyList<WatchTriggerChoice> Triggers { get; } =
    [
        new(WatchTrigger.OnLaunch, "On launch"),
        new(WatchTrigger.WaitForWindow, "Wait for window"),
        new(WatchTrigger.WaitForDirect3D, "Wait for Direct3D")
    ];

    [ObservableProperty]
    private string _target;

    [ObservableProperty]
    private WatchTrigger _trigger;

    [ObservableProperty]
    private string _delayText;

    [ObservableProperty]
    private string _validationMessage = "";

    public string DllSummary { get; }

    public WatchConfig? Result { get; private set; }

    public bool TryAccept()
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            ValidationMessage = "Choose an executable to watch.";
            return false;
        }

        if (!int.TryParse(DelayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay))
        {
            ValidationMessage = "Delay must be a whole number of milliseconds.";
            return false;
        }

        Result = new WatchConfig
        {
            Target = Target.Trim(),
            Trigger = Trigger,
            DelayMs = WatchPolicy.ClampDelayMs(delay)
        };
        ValidationMessage = "";
        return true;
    }
}

public sealed record WatchTriggerChoice(WatchTrigger Trigger, string Label);
