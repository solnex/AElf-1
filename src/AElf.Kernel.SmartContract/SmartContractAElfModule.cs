using System.Runtime.CompilerServices;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Miner.Application;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.SmartContract.Infrastructure;
using AElf.Modularity;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Threading;

[assembly: InternalsVisibleTo("AElf.Kernel.SmartContract.Tests")]
namespace AElf.Kernel.SmartContract
{
    [DependsOn(typeof(CoreKernelAElfModule))]
    public class SmartContractAElfModule: AElfModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            context.Services.AddSingleton<ISmartContractRunnerContainer, SmartContractRunnerContainer>();
            context.Services.AddSingleton<ITransactionSizeFeeUnitPriceProvider, DefaultTransactionSizeFeeUnitPriceProvider>();
            context.Services.AddSingleton<ICalculateStoCostStrategy, DefaultCalculateStoCostStrategy>();
            context.Services.AddSingleton<ICalculateCpuCostStrategy, DefaultCalculateCpuCostStrategy>();
            context.Services.AddSingleton<ICalculateNetCostStrategy, DefaultCalculateNetCostStrategy>();
            context.Services.AddSingleton<ICalculateRamCostStrategy, DefaultCalculateRamCostStrategy>();
            context.Services.AddSingleton<ICalculateTxCostStrategy, DefaultCalculateTxCostStrategy>();
            //context.Services.AddSingleton<ICalculateFeeService, DefaultCalculateFeeService>();
        }
        
        public override void OnPostApplicationInitialization(ApplicationInitializationContext context)
        {
            var deployedContractAddressService = context.ServiceProvider.GetService<IDeployedContractAddressService>();
            AsyncHelper.RunSync(() => deployedContractAddressService.InitAsync());
        }
    }
}