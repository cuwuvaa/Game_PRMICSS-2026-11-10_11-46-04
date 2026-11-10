using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;

// Unity Transport 6.5 / NGO 2.13: the old UnityTransport.SetDebugSimulatorParameters is a no-op.
// Configure the actual UDP network layer, below reliable delivery, on BOTH endpoints.
public sealed class LabNetworkSimulator : INetworkStreamDriverConstructor
{
    private readonly uint delay;
    private readonly float loss;
    public LabNetworkSimulator(int profile)
    {
        delay = profile == 1 ? 100u : profile == 2 ? 175u : 0u;
        loss = profile == 2 ? 4f : 0f;
    }
    public void CreateDriver(UnityTransport transport, out NetworkDriver driver,
        out NetworkPipeline unreliableFragmentedPipeline,
        out NetworkPipeline unreliableSequencedFragmentedPipeline,
        out NetworkPipeline reliableSequencedPipeline)
    {
        var settings = transport.GetDefaultNetworkSettings();
        try
        {
            settings.WithNetworkSimulatorParameters(sendDelayMS: delay, sendPacketLossPercent: loss);
            driver = NetworkDriver.Create(new UDPNetworkInterface(), settings);
        }
        finally { settings.Dispose(); }
        transport.GetDefaultPipelineConfigurations(ref driver, out var unreliable,
            out var sequenced, out var reliable);
        unreliableFragmentedPipeline = driver.CreatePipeline(unreliable);
        unreliableSequencedFragmentedPipeline = driver.CreatePipeline(sequenced);
        reliableSequencedPipeline = driver.CreatePipeline(reliable);
    }
}
