using System.Threading.Tasks;
using AElf.Kernel.Blockchain;
using AElf.Kernel.Configuration;
using AElf.Kernel.TransactionPool.Application;
using AElf.Kernel.Txn.Application;
using Google.Protobuf.WellKnownTypes;
using AElf.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AElf.Kernel.Miner.Application
{
    public class MinerService : IMinerService
    {
        public ILogger<MinerService> Logger { get; set; }
        private readonly ITransactionPoolService _transactionPoolService;
        private readonly IMiningService _miningService;
        private readonly IBlockTransactionLimitProvider _blockTransactionLimitProvider;
        private readonly ITransactionPackingOptionProvider _transactionPackingOptionProvider;

        public MinerService(IMiningService miningService,
            IBlockTransactionLimitProvider blockTransactionLimitProvider,
            ITransactionPackingOptionProvider transactionPackingOptionProvider,
            ITransactionPoolService transactionPoolService)
        {
            _miningService = miningService;
            _blockTransactionLimitProvider = blockTransactionLimitProvider;
            _transactionPackingOptionProvider = transactionPackingOptionProvider;
            _transactionPoolService = transactionPoolService;

            Logger = NullLogger<MinerService>.Instance;
        }

        /// <inheritdoc />
        /// <summary>
        /// Mine process.
        /// </summary>
        /// <returns></returns>
        public async Task<BlockExecutedSet> MineAsync(Hash previousBlockHash, long previousBlockHeight,
            Timestamp blockTime,
            Duration blockExecutionTime)
        {
            var chainContext = new ChainContext
            {
                BlockHash = previousBlockHash,
                BlockHeight = previousBlockHeight
            };
            var limit = await _blockTransactionLimitProvider.GetLimitAsync(chainContext);
            var executableTransactionSet = await _transactionPoolService.GetExecutableTransactionSetAsync(
                previousBlockHash,
                _transactionPackingOptionProvider.IsTransactionPackable(chainContext)
                    ? limit
                    : -1);

            Logger.LogInformation(
                $"Start mining with previous hash: {previousBlockHash}, previous height: {previousBlockHeight}.");
            return await _miningService.MineAsync(
                new RequestMiningDto
                {
                    PreviousBlockHash = previousBlockHash,
                    PreviousBlockHeight = previousBlockHeight,
                    BlockExecutionTime = blockExecutionTime
                }, executableTransactionSet.Transactions, blockTime);
        }
    }
}