using NAudio.CoreAudioApi;
using Serilog;

namespace AudioInOutTranscribing.App.Audio;

public sealed class DeviceEnumerator
{
    public IReadOnlyList<DeviceDescriptor> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new DeviceDescriptor(d.ID, d.FriendlyName))
            .ToList();
    }

    public IReadOnlyList<DeviceDescriptor> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new DeviceDescriptor(d.ID, d.FriendlyName))
            .ToList();
    }

    public MMDevice? ResolveInputDevice(string? preferredId, string? preferredName)
    {
        return ResolveDevice(DataFlow.Capture, Role.Communications, preferredId, preferredName);
    }

    public MMDevice? ResolveOutputDevice(string? preferredId, string? preferredName)
    {
        return ResolveDevice(DataFlow.Render, Role.Multimedia, preferredId, preferredName);
    }

    private static MMDevice? ResolveDevice(
        DataFlow flow,
        Role defaultRole,
        string? preferredId,
        string? preferredName)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var candidates = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);

            var byId = candidates.FirstOrDefault(d => string.Equals(d.ID, preferredId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }

            var byName = candidates.FirstOrDefault(d =>
                string.Equals(d.FriendlyName, preferredName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }

            return enumerator.GetDefaultAudioEndpoint(flow, defaultRole);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve audio device. flow={Flow}", flow);
            return null;
        }
    }
}
