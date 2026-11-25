using ProbeLab;

// Usage:
//   dotnet run --project tools/ProbeLab -c Release -- contracts
//   dotnet run --project tools/ProbeLab -c Release -- live <path to HealthChecksDemo.dll>
Console.WriteLine($"# ProbeLab {args.FirstOrDefault()} on .NET {Environment.Version}");
switch (args)
{
    case ["contracts"]:
        await Contracts.RunAsync();
        break;
    case ["live", string appDll]:
        await LiveRuns.RunAsync(Path.GetFullPath(appDll));
        break;
    default:
        Console.Error.WriteLine("Usage: ProbeLab contracts | ProbeLab live <HealthChecksDemo.dll>");
        return 1;
}

return 0;
