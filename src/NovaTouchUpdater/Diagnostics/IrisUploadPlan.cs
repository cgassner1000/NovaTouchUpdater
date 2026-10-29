namespace NovaTouchUpdater;

internal static class IrisUploadPlan
{
    internal static IrisInstanceInfo[] UploadInstances(IrisInventoryResult inventory)
    {
        if (inventory.Warnings.Length > 0) throw new InvalidDataException("IRIS-Instanzen konnten nicht vollständig ermittelt werden. BETA: Database Check prüfen.");
        foreach (IrisInstanceInfo instance in inventory.Instances)
        {
            if (instance.Error.Length > 0) throw new InvalidDataException($"IRIS-Instanz {instance.Name}: {instance.Error}");
            if (instance.Namespaces.Length > 0 && instance.SuperServerPort is < 1 or > 65535)
                throw new InvalidDataException($"IRIS-Instanz {instance.Name}: {instance.ConnectionError}");
        }
        return inventory.Instances.Where(instance => instance.Namespaces.Any(name => !name.Equals("%SYS", StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    internal static IrisUploadTarget[] Create(IrisInventoryResult inventory, bool useTls) =>
        UploadInstances(inventory).Select(instance =>
        {
            return new IrisUploadTarget { Instance = instance.Name, Directory = instance.Directory, Port = instance.SuperServerPort,
                Namespaces = instance.Namespaces.Where(name => !name.Equals("%SYS", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                User = "_SYSTEM", Password = "SYS", Ssl = useTls };
        }).ToArray();
}
