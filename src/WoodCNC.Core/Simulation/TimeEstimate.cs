namespace WoodCNC.Core.Simulation;

public sealed record TimeEstimate(
    double RapidSeconds,
    double CuttingSeconds,
    double DwellSeconds)
{
    public double TotalSeconds => RapidSeconds + CuttingSeconds + DwellSeconds;
}

