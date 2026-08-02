namespace Vela.Core.Contracts;

public interface IWslInventoryReader
{
    Task<WslInventory> GetInstalledInventoryAsync(CancellationToken cancellationToken);

    Task<WslInventory> GetRunningInventoryAsync(CancellationToken cancellationToken);
}
