using System.Collections.Generic;
using System.Linq;
using Acs1;
using Acs3;
using Acs7;
using AElf.Contracts.Association;
using AElf.Contracts.Consensus.AEDPoS;
using AElf.Contracts.MultiToken;
using AElf.CSharp.Core.Extension;
using AElf.CSharp.Core.Utils;
using AElf.Sdk.CSharp;
using AElf.Sdk.CSharp.State;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace AElf.Contracts.CrossChain
{
    public partial class CrossChainContract
    {
        private const string ConsensusExtraDataName = "Consensus";

        /// <summary>
        /// Bind parent chain height together with self height.
        /// </summary>
        /// <param name="childHeight"></param>
        /// <param name="parentHeight"></param>
        private void BindParentChainHeight(long childHeight, long parentHeight)
        {
            Assert(State.ChildHeightToParentChainHeight[childHeight] == 0,
                $"Already bound at height {childHeight} with parent chain");
            State.ChildHeightToParentChainHeight[childHeight] = parentHeight;
        }

        private Hash ComputeRootWithTransactionStatusMerklePath(Hash txId, MerklePath path)
        {
            var txResultStatusRawBytes =
                EncodingHelper.EncodeUtf8(TransactionResultStatus.Mined.ToString());
            var hash = HashHelper.ComputeFrom(txId.ToByteArray().Concat(txResultStatusRawBytes).ToArray());
            return path.ComputeRootWithLeafNode(hash);
        }

        private Hash ComputeRootWithMultiHash(IEnumerable<Hash> nodes)
        {
            return BinaryMerkleTree.FromLeafNodes(nodes).Root;
        }

        /// <summary>
        /// Record merkle path of self chain block, which is from parent chain. 
        /// </summary>
        /// <param name="height"></param>
        /// <param name="path"></param>
        private void AddIndexedTxRootMerklePathInParentChain(long height, MerklePath path)
        {
            var existing = State.TxRootMerklePathInParentChain[height];
            Assert(existing == null,
                $"Merkle path already bound at height {height}.");
            State.TxRootMerklePathInParentChain[height] = path;
        }

        private void ChargeSideChainIndexingFee(Address lockAddress, long amount, int chainId)
        {
            if (amount <= 0)
                return;
            TransferFrom(new TransferFromInput
            {
                From = lockAddress,
                To = Context.ConvertVirtualAddressToContractAddress(ConvertChainIdToHash(chainId)),
                Amount = amount,
                Symbol = Context.Variables.NativeSymbol
            });
        }

        private void UnlockTokenAndResource(SideChainInfo sideChainInfo)
        {
            // unlock token
            var chainId = sideChainInfo.SideChainId;
            var balance = GetSideChainIndexingFeeDeposit(chainId);
            if (balance <= 0)
                return;
            TransferDepositToken(new TransferInput
            {
                To = sideChainInfo.Proposer,
                Amount = balance,
                Symbol = Context.Variables.NativeSymbol
            }, chainId);
        }

        private long GetSideChainIndexingFeeDeposit(int chainId)
        {
            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            var balanceOutput = State.TokenContract.GetBalance.Call(new GetBalanceInput
            {
                Owner = Context.ConvertVirtualAddressToContractAddress(ConvertChainIdToHash(chainId)),
                Symbol = Context.Variables.NativeSymbol
            });
            
            return balanceOutput.Balance;
        }
        
        private void AssertValidSideChainCreationRequest(SideChainCreationRequest sideChainCreationRequest,
            Address proposer)
        {
            var proposedRequest = State.ProposedSideChainCreationRequestState[Context.Sender];
            Assert(proposedRequest == null || Context.CurrentBlockTime >= proposedRequest.ExpiredTime,
                "Request side chain creation failed.");

            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            var allowance = State.TokenContract.GetAllowance.Call(new GetAllowanceInput
            {
                Owner = proposer,
                Spender = Context.Self,
                Symbol = Context.Variables.NativeSymbol
            }).Allowance;
            
            Assert(
                allowance >= sideChainCreationRequest.LockedTokenAmount,
                "Allowance not enough.");
            
            Assert(
                sideChainCreationRequest.IndexingPrice >= 0 &&
                sideChainCreationRequest.LockedTokenAmount >= sideChainCreationRequest.IndexingPrice,
                "Invalid chain creation request.");
            
            if (!sideChainCreationRequest.IsPrivilegePreserved) 
                return; // there is no restriction for non-exclusive side chain creation
            
            AssertValidResourceTokenAmount(sideChainCreationRequest);

            if (!IsPrimaryTokenNeeded(sideChainCreationRequest)) 
                return;
            
            // assert primary token to create
            AssertValidSideChainTokenInfo(sideChainCreationRequest.SideChainTokenSymbol,
                sideChainCreationRequest.SideChainTokenName,
                sideChainCreationRequest.SideChainTokenTotalSupply);
            Assert(sideChainCreationRequest.SideChainTokenInitialIssueList.Count > 0 &&
                   sideChainCreationRequest.SideChainTokenInitialIssueList.All(issue => issue.Amount > 0),
                "Invalid side chain token initial issue list.");
        }

        private void AssertValidResourceTokenAmount(SideChainCreationRequest sideChainCreationRequest)
        {
            var resourceTokenMap = sideChainCreationRequest.InitialResourceAmount;
            foreach (var resourceTokenSymbol in Context.Variables.GetStringArray(PayRentalSymbolListName))
            {
                Assert(resourceTokenMap.ContainsKey(resourceTokenSymbol) && resourceTokenMap[resourceTokenSymbol] > 0,
                    "Invalid side chain resource token request.");
            }
        }

        private void AssertValidSideChainTokenInfo(string symbol, string tokenName, long totalSupply)
        {
            Assert(!string.IsNullOrEmpty(symbol) && !string.IsNullOrEmpty(tokenName), "Invalid side chain token name.");
            Assert(totalSupply > 0, "Invalid side chain token supply.");
        }

        private void SetContractStateRequired(ContractReferenceState state, string contractSystemName)
        {
            if (state.Value != null)
                return;
            state.Value = Context.GetContractAddressByName(contractSystemName);
        }

        private void TransferDepositToken(TransferInput input, int chainId)
        {
            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            Context.SendVirtualInline(ConvertChainIdToHash(chainId), State.TokenContract.Value,
                nameof(State.TokenContract.Transfer), input);
        }

        private void TransferFrom(TransferFromInput input)
        {
            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            State.TokenContract.TransferFrom.Send(input);
        }

        private void CreateSideChainToken(SideChainCreationRequest sideChainCreationRequest, int chainId,
            Address creator)
        {
            if (!IsPrimaryTokenNeeded(sideChainCreationRequest))
                return;

            // new token needed only for exclusive side chain
            var sideChainTokenInfo = new SideChainTokenInfo
            {
                TokenName = sideChainCreationRequest.SideChainTokenName,
                Symbol = sideChainCreationRequest.SideChainTokenSymbol,
                TotalSupply = sideChainCreationRequest.SideChainTokenTotalSupply,
                Decimals = sideChainCreationRequest.SideChainTokenDecimals,
                IsBurnable = sideChainCreationRequest.IsSideChainTokenBurnable,
                IsProfitable = sideChainCreationRequest.IsSideChainTokenProfitable
            };
            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            State.TokenContract.Create.Send(new CreateInput
            {
                TokenName = sideChainTokenInfo.TokenName,
                Decimals = sideChainTokenInfo.Decimals,
                IsBurnable = sideChainTokenInfo.IsBurnable,
                Issuer = creator,
                IssueChainId = chainId,
                Symbol = sideChainTokenInfo.Symbol,
                TotalSupply = sideChainTokenInfo.TotalSupply,
                IsProfitable = sideChainTokenInfo.IsProfitable
            });
        }

        private TokenInfo GetNativeTokenInfo()
        {
            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            return State.TokenContract.GetNativeTokenInfo.Call(new Empty());
        }

        private TokenInfo GetTokenInfo(string symbol)
        {
            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            return State.TokenContract.GetTokenInfo.Call(new GetTokenInfoInput
            {
                Symbol = symbol
            });
        }

        private TokenInfoList GetResourceTokenInfo()
        {
            SetContractStateRequired(State.TokenContract, SmartContractConstants.TokenContractSystemName);
            return State.TokenContract.GetResourceTokenInfo.Call(new Empty());
        }

        private MinerListWithRoundNumber GetCurrentMiners()
        {
            SetContractStateRequired(State.ConsensusContract, SmartContractConstants.ConsensusContractSystemName);
            var miners = State.ConsensusContract.GetCurrentMinerListWithRoundNumber.Call(new Empty());
            return miners;
        }

        // only for side chain
        private void UpdateCurrentMiners(ByteString bytes)
        {
            SetContractStateRequired(State.ConsensusContract, SmartContractConstants.ConsensusContractSystemName);
            State.ConsensusContract.UpdateConsensusInformation.Send(new ConsensusInformation {Value = bytes});
        }

        private Hash GetParentChainMerkleTreeRoot(long parentChainHeight)
        {
            return State.ParentChainTransactionStatusMerkleTreeRoot[parentChainHeight];
        }

        private Hash GetSideChainMerkleTreeRoot(long parentChainHeight)
        {
            var indexedSideChainData = State.IndexedSideChainBlockData[parentChainHeight];
            return ComputeRootWithMultiHash(
                indexedSideChainData.SideChainBlockDataList.Select(d => d.TransactionStatusMerkleTreeRoot));
        }

        private Hash GetCousinChainMerkleTreeRoot(long parentChainHeight)
        {
            return State.TransactionMerkleTreeRootRecordedInParentChain[parentChainHeight];
        }

        private Hash GetMerkleTreeRoot(int chainId, long parentChainHeight)
        {
            if (chainId == State.ParentChainId.Value)
            {
                // it is parent chain
                return GetParentChainMerkleTreeRoot(parentChainHeight);
            }

            if (State.SideChainInfo[chainId] != null)
            {
                // it is child chain
                return GetSideChainMerkleTreeRoot(parentChainHeight);
            }

            return GetCousinChainMerkleTreeRoot(parentChainHeight);
        }

        private AuthorityInfo GetCrossChainIndexingController()
        {
            return State.CrossChainIndexingController.Value;
        }

        private AuthorityInfo GetSideChainLifetimeController()
        {
            return State.SideChainLifetimeController.Value;
        }

        private void AssertCrossChainIndexingControllerAuthority(Address address)
        {
            var crossChainIndexingController = GetCrossChainIndexingController();
            Assert(crossChainIndexingController.OwnerAddress == address, "Unauthorized behavior.");
        }

        private void AssertSideChainLifetimeControllerAuthority(Address address)
        {
            var sideChainLifetimeController = GetSideChainLifetimeController();
            Assert(sideChainLifetimeController.OwnerAddress == address, "Unauthorized behavior.");
        }

        private void AssertAddressIsCurrentMiner(Address address)
        {
            SetContractStateRequired(State.ConsensusContract, SmartContractConstants.ConsensusContractSystemName);
            var isCurrentMiner = State.ConsensusContract.IsCurrentMiner.Call(address).Value;
            Assert(isCurrentMiner, "No permission.");
        }

        private void AssertParentChainBlock(int parentChainId, long currentRecordedHeight,
            ParentChainBlockData parentChainBlockData)
        {
            Assert(parentChainId == parentChainBlockData.ChainId, "Wrong parent chain id.");
            Assert(currentRecordedHeight + 1 == parentChainBlockData.Height,
                $"Parent chain block info at height {currentRecordedHeight + 1} is needed, not {parentChainBlockData.Height}");
            Assert(parentChainBlockData.TransactionStatusMerkleTreeRoot != null,
                "Parent chain transaction status merkle tree root needed.");
        }

        private void AssertIsCrossChainBlockDataToBeReleased(RecordCrossChainDataInput recordCrossChainDataInput)
        {
            var pendingProposalExists = TryGetProposalWithStatus(CrossChainIndexingProposalStatus.ToBeReleased,
                out var pendingCrossChainIndexingProposal);
            Assert(
                pendingProposalExists &&
                pendingCrossChainIndexingProposal.ProposedCrossChainBlockData.Equals(recordCrossChainDataInput
                    .ProposedCrossChainData) &&
                pendingCrossChainIndexingProposal.Proposer == recordCrossChainDataInput.Proposer,
                "Incorrect cross chain indexing proposal status.");
            State.CrossChainIndexingProposal.Value = new CrossChainIndexingProposal();
        }

        private int GetChainId(long serialNumber)
        {
            return ChainHelper.GetChainId(serialNumber + Context.ChainId);
        }

        private SideChainCreationRequestState ProposeNewSideChain(SideChainCreationRequest request, Address proposer)
        {
            var sideChainLifeTimeController = GetSideChainLifetimeController();
            var proposalCreationInput = new CreateProposalBySystemContractInput
            {
                ProposalInput =
                    new CreateProposalInput
                    {
                        ContractMethodName = nameof(CreateSideChain),
                        ToAddress = Context.Self,
                        ExpiredTime =
                            Context.CurrentBlockTime.AddSeconds(SideChainCreationProposalExpirationTimePeriod),
                        Params = new CreateSideChainInput {SideChainCreationRequest = request, Proposer = proposer}
                            .ToByteString(),
                        OrganizationAddress = sideChainLifeTimeController.OwnerAddress
                    },
                OriginProposer = Context.Sender
            };
            Context.SendInline(sideChainLifeTimeController.ContractAddress,
                nameof(AuthorizationContractContainer.AuthorizationContractReferenceState
                    .CreateProposalBySystemContract), proposalCreationInput);
            var sideChainCreationRequest = new SideChainCreationRequestState()
            {
                SideChainCreationRequest = request,
                ExpiredTime = proposalCreationInput.ProposalInput.ExpiredTime,
                Proposer = proposer
            };
            return sideChainCreationRequest;
        }

        private void ProposeCrossChainBlockData(CrossChainBlockData crossChainBlockData, Address proposer)
        {
            var crossChainIndexingController = GetCrossChainIndexingController();
            var proposalToken = Context.PreviousBlockHash;
            var proposalCreationInput = new CreateProposalBySystemContractInput
            {
                ProposalInput = new CreateProposalInput
                {
                    Params = new RecordCrossChainDataInput
                    {
                        ProposedCrossChainData = crossChainBlockData,
                        Proposer = proposer
                    }.ToByteString(),
                    ContractMethodName = nameof(RecordCrossChainData),
                    ExpiredTime = Context.CurrentBlockTime.AddSeconds(CrossChainIndexingProposalExpirationTimePeriod),
                    OrganizationAddress = crossChainIndexingController.OwnerAddress,
                    ToAddress = Context.Self,
                    Token = proposalToken
                },
                OriginProposer = Context.Sender
            };

            Context.SendInline(crossChainIndexingController.ContractAddress,
                nameof(AuthorizationContractContainer.AuthorizationContractReferenceState
                    .CreateProposalBySystemContract), proposalCreationInput);
            var crossChainIndexingProposal = new CrossChainIndexingProposal
            {
                Proposer = proposer,
                ProposedCrossChainBlockData = crossChainBlockData
            };
            var proposalId = Context.GenerateId(crossChainIndexingController.ContractAddress, proposalToken);
            crossChainIndexingProposal.ProposalId = proposalId;
            SetCrossChainIndexingProposalStatus(crossChainIndexingProposal, CrossChainIndexingProposalStatus.Pending);
            Context.Fire(new CrossChainIndexingDataProposedEvent
            {
                ProposedCrossChainData = crossChainBlockData
            });
        }

        private ProposalOutput GetCrossChainProposal(AuthorityInfo authorityInfo, Hash proposalId)
        {
            return Context.Call<ProposalOutput>(authorityInfo.ContractAddress,
                nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.GetProposal), proposalId);
        }

        private void HandleIndexingProposal(Hash proposalId, CrossChainIndexingProposal crossChainIndexingProposal)
        {
            var crossChainIndexingController = GetCrossChainIndexingController();
            var proposal = GetCrossChainProposal(crossChainIndexingController, proposalId);
            Assert(proposal.ToBeReleased, "Not approved cross chain indexing proposal.");
            Context.SendInline(crossChainIndexingController.ContractAddress,
                nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.Release),
                proposal.ProposalId); // release if ready
            SetCrossChainIndexingProposalStatus(crossChainIndexingProposal,
                CrossChainIndexingProposalStatus.ToBeReleased);
        }

        private void AssertValidCrossChainDataBeforeIndexing(CrossChainBlockData crossChainBlockData)
        {
            Assert(
                crossChainBlockData.ParentChainBlockDataList.Count > 0 ||
                crossChainBlockData.SideChainBlockDataList.Count > 0,
                "Empty cross chain data proposed.");
            Assert(ValidateSideChainBlockData(crossChainBlockData.SideChainBlockDataList) &&
                   ValidateParentChainBlockData(crossChainBlockData.ParentChainBlockDataList),
                "Invalid cross chain data to be indexed.");
        }

        private bool TryGetProposalWithStatus(CrossChainIndexingProposalStatus status,
            out CrossChainIndexingProposal proposal)
        {
            proposal = State.CrossChainIndexingProposal.Value;
            return proposal != null && proposal.Status == status;
        }

        private void ResetCrossChainIndexingProposal()
        {
            // clear pending proposal
            SetCrossChainIndexingProposalStatus(new CrossChainIndexingProposal(),
                CrossChainIndexingProposalStatus.NonProposed);
        }

        private void SetCrossChainIndexingProposalStatus(CrossChainIndexingProposal crossChainIndexingProposal,
            CrossChainIndexingProposalStatus status)
        {
            crossChainIndexingProposal.Status = status;
            State.CrossChainIndexingProposal.Value = crossChainIndexingProposal;
        }

        private void BanCrossChainIndexingFromAddress(Address address)
        {
            State.BannedMinerHeight[address] = Context.CurrentHeight;
        }

        private void ClearCrossChainIndexingProposalIfExpired()
        {
            var crossChainIndexingProposal = State.CrossChainIndexingProposal.Value;
            if (crossChainIndexingProposal.Status == CrossChainIndexingProposalStatus.NonProposed)
                return;

            var isExpired = CheckProposalExpired(GetCrossChainIndexingController(), crossChainIndexingProposal.ProposalId);
            Assert(isExpired, "Unable to clear cross chain indexing proposal not expired.");
            //            BanCrossChainIndexingFromAddress(crossChainIndexingProposal.Proposer); // ban the proposer if expired
            ResetCrossChainIndexingProposal();
        }

        private bool TryClearExpiredSideChainCreationRequestProposal(Hash proposalId, Address proposer)
        {
            var isExpired = CheckProposalExpired(GetSideChainLifetimeController(), proposalId);
            if (isExpired)
                State.ProposedSideChainCreationRequestState.Remove(proposer);
            return isExpired;
        }

        private bool CheckProposalExpired(AuthorityInfo authorityInfo, Hash proposalId)
        {
            var proposalInfo = GetCrossChainProposal(authorityInfo, proposalId);
            return proposalInfo.ExpiredTime <= Context.CurrentBlockTime;
        }

        private void CreateInitialOrganizationForInitialControllerAddress()
        {
            SetContractStateRequired(State.ParliamentContract, SmartContractConstants.ParliamentContractSystemName);
            var proposalReleaseThreshold = new ProposalReleaseThreshold
            {
                MinimalApprovalThreshold = DefaultMinimalApprovalThreshold,
                MinimalVoteThreshold = DefaultMinimalVoteThresholdThreshold,
                MaximalAbstentionThreshold = DefaultMaximalAbstentionThreshold,
                MaximalRejectionThreshold = DefaultMaximalRejectionThreshold
            };
            State.ParliamentContract.CreateOrganizationBySystemContract.Send(
                new Parliament.CreateOrganizationBySystemContractInput
                {
                    OrganizationCreationInput = new Parliament.CreateOrganizationInput
                    {
                        ProposalReleaseThreshold = proposalReleaseThreshold,
                        ProposerAuthorityRequired = false,
                        ParliamentMemberProposingAllowed = true
                    },
                    OrganizationAddressFeedbackMethod = nameof(SetInitialSideChainLifetimeControllerAddress)
                });

            State.ParliamentContract.CreateOrganizationBySystemContract.Send(
                new Parliament.CreateOrganizationBySystemContractInput
                {
                    OrganizationCreationInput = new Parliament.CreateOrganizationInput
                    {
                        ProposalReleaseThreshold = proposalReleaseThreshold,
                        ProposerAuthorityRequired = true,
                        ParliamentMemberProposingAllowed = true
                    },
                    OrganizationAddressFeedbackMethod = nameof(SetInitialIndexingControllerAddress)
                });
        }

        private CreateOrganizationInput GenerateOrganizationInputForIndexingFeePrice(
            IList<Address> organizationMembers)
        {
            var createOrganizationInput = new CreateOrganizationInput
            {
                ProposerWhiteList = new ProposerWhiteList
                {
                    Proposers = {organizationMembers}
                },
                OrganizationMemberList = new OrganizationMemberList
                {
                    OrganizationMembers = {organizationMembers}
                },
                ProposalReleaseThreshold = new ProposalReleaseThreshold
                {
                    MinimalApprovalThreshold = organizationMembers.ToList().Count,
                    MinimalVoteThreshold = organizationMembers.ToList().Count,
                    MaximalRejectionThreshold = 0,
                    MaximalAbstentionThreshold = 0
                }
            };
            return createOrganizationInput;
        }

        private Address CalculateSideChainIndexingFeeControllerOrganizationAddress(CreateOrganizationInput input)
        {
            SetContractStateRequired(State.AssociationContract, SmartContractConstants.AssociationContractSystemName);
            var address = State.AssociationContract.CalculateOrganizationAddress.Call(input);
            return address;
        }

        private AuthorityInfo CreateDefaultOrganizationForIndexingFeePriceManagement(Address sideChainCreator)
        {
            var createOrganizationInput =
                GenerateOrganizationInputForIndexingFeePrice(new List<Address>
                {
                    sideChainCreator,
                    GetCrossChainIndexingController().OwnerAddress
                });
            SetContractStateRequired(State.AssociationContract, SmartContractConstants.AssociationContractSystemName);
            State.AssociationContract.CreateOrganization.Send(createOrganizationInput);

            var controllerAddress = CalculateSideChainIndexingFeeControllerOrganizationAddress(createOrganizationInput);
            return new AuthorityInfo
            {
                ContractAddress = State.AssociationContract.Value,
                OwnerAddress = controllerAddress
            };
        }

        private bool ValidateAuthorityInfoExists(AuthorityInfo authorityInfo)
        {
            return Context.Call<BoolValue>(authorityInfo.ContractAddress,
                nameof(AuthorizationContractContainer.AuthorizationContractReferenceState.ValidateOrganizationExist),
                authorityInfo.OwnerAddress).Value;
        }

        private bool ValidateParliamentOrganization(Address organizationAddress,
            bool isParliamentMemberProposingRequired)
        {
            SetContractStateRequired(State.ParliamentContract, SmartContractConstants.ParliamentContractSystemName);
            var organization = State.ParliamentContract.GetOrganization.Call(organizationAddress);
            return organization != null &&
                   (!isParliamentMemberProposingRequired || organization.ParliamentMemberProposingAllowed);
        }

        private bool ValidateSideChainBlockData(IEnumerable<SideChainBlockData> sideChainBlockData)
        {
            var groupResult = sideChainBlockData.GroupBy(data => data.ChainId, data => data);

            foreach (var group in groupResult)
            {
                var chainId = group.Key;
                var info = State.SideChainInfo[chainId];
                if (info == null || info.SideChainStatus == SideChainStatus.Terminated)
                    return false;
                var currentSideChainHeight = State.CurrentSideChainHeight[chainId];
                var target = currentSideChainHeight != 0
                    ? currentSideChainHeight + 1
                    : AElfConstants.GenesisBlockHeight;
                // indexing fee
                // var indexingPrice = info.SideChainCreationRequest.IndexingPrice;
                // var lockedToken = State.IndexingBalance[chainId];
                foreach (var blockData in group)
                {
                    var sideChainHeight = blockData.Height;
                    if (target != sideChainHeight)
                        return false;
                    target++;
                }
            }

            return true;
        }

        private bool ValidateParentChainBlockData(IEnumerable<ParentChainBlockData> parentChainBlockData)
        {
            var parentChainId = State.ParentChainId.Value;
            var currentHeight = State.CurrentParentChainHeight.Value;
            foreach (var blockData in parentChainBlockData)
            {
                if (parentChainId != blockData.ChainId || currentHeight + 1 != blockData.Height ||
                    blockData.TransactionStatusMerkleTreeRoot == null)
                    return false;
                if (blockData.IndexedMerklePath.Any(indexedBlockInfo =>
                    State.ChildHeightToParentChainHeight[indexedBlockInfo.Key] != 0 ||
                    State.TxRootMerklePathInParentChain[indexedBlockInfo.Key] != null))
                    return false;

                currentHeight += 1;
            }

            return true;
        }

        private bool IsPrimaryTokenNeeded(SideChainCreationRequest sideChainCreationRequest)
        {
            // there won't be new token creation if it is secondary side chain
            // or the side chain is not exclusive
            return sideChainCreationRequest.IsPrivilegePreserved && !IsParentChainExist();
        }

        private bool IsParentChainExist()
        {
            return State.ParentChainId.Value != 0;
        }

        /// <summary>
        /// Index parent chain block data.
        /// </summary>
        /// <param name="parentChainBlockData"></param>
        private IndexedParentChainBlockData IndexParentChainBlockData(IList<ParentChainBlockData> parentChainBlockData)
        {
            var parentChainId = State.ParentChainId.Value;
            var currentHeight = State.CurrentParentChainHeight.Value;
            var indexedParentChainBlockData = new IndexedParentChainBlockData
            {
                LocalChainHeight = Context.CurrentHeight
            };
            for (var i = 0; i < parentChainBlockData.Count; i++)
            {
                var blockInfo = parentChainBlockData[i];
                AssertParentChainBlock(parentChainId, currentHeight, blockInfo);
                long parentChainHeight = blockInfo.Height;
                State.ParentChainTransactionStatusMerkleTreeRoot[parentChainHeight] =
                    blockInfo.TransactionStatusMerkleTreeRoot;
                foreach (var indexedBlockInfo in blockInfo.IndexedMerklePath)
                {
                    BindParentChainHeight(indexedBlockInfo.Key, parentChainHeight);
                    AddIndexedTxRootMerklePathInParentChain(indexedBlockInfo.Key, indexedBlockInfo.Value);
                }

                // send consensus data shared from main chain  
                if (i == parentChainBlockData.Count - 1 &&
                    blockInfo.ExtraData.TryGetValue(ConsensusExtraDataName, out var bytes))
                {
                    Context.LogDebug(() => "Updating consensus information..");
                    UpdateCurrentMiners(bytes);
                }

                if (blockInfo.CrossChainExtraData != null)
                    State.TransactionMerkleTreeRootRecordedInParentChain[parentChainHeight] =
                        blockInfo.CrossChainExtraData.TransactionStatusMerkleTreeRoot;

                indexedParentChainBlockData.ParentChainBlockDataList.Add(blockInfo);
                currentHeight += 1;
            }

            State.CurrentParentChainHeight.Value = currentHeight;
            return indexedParentChainBlockData;
        }

        /// <summary>
        /// Index side chain block data.
        /// </summary>
        /// <param name="sideChainBlockDataList">Side chain block data to be indexed.</param>
        /// <param name="proposer">Charge indexing fee for the one who proposed side chain block data.</param>
        /// <returns>Valid side chain block data which are indexed.</returns>
        private IndexedSideChainBlockData IndexSideChainBlockData(IList<SideChainBlockData> sideChainBlockDataList,
            Address proposer)
        {
            var indexedSideChainBlockData = new IndexedSideChainBlockData();
            var groupResult = sideChainBlockDataList.GroupBy(data => data.ChainId, data => data);
            var formattedProposerAddress = proposer.ToByteString().ToBase64();
            foreach (var group in groupResult)
            {
                long indexingFeeAmount = 0;
                var chainId = group.Key;

                var sideChainInfo = State.SideChainInfo[chainId];
                if (sideChainInfo == null)
                    continue;
                var currentSideChainHeight = State.CurrentSideChainHeight[chainId];
                long arrearsAmount = 0;
                var lockedToken = sideChainInfo.SideChainStatus == SideChainStatus.IndexingFeeDebt
                    ? 0
                    : GetSideChainIndexingFeeDeposit(chainId);

                foreach (var sideChainBlockData in group)
                {
                    var target = currentSideChainHeight != 0
                        ? currentSideChainHeight + 1
                        : AElfConstants.GenesisBlockHeight;
                    var sideChainHeight = sideChainBlockData.Height;
                    if (target != sideChainHeight)
                        break;

                    // indexing fee
                    var indexingPrice = sideChainInfo.IndexingPrice;

                    lockedToken -= indexingPrice;

                    if (lockedToken < 0)
                    {
                        // record arrears
                        arrearsAmount += indexingPrice;
                        sideChainInfo.SideChainStatus = SideChainStatus.IndexingFeeDebt;
                    }
                    else
                    {
                        indexingFeeAmount += indexingPrice;
                    }

                    currentSideChainHeight++;
                    indexedSideChainBlockData.SideChainBlockDataList.Add(sideChainBlockData);
                }
                
                if (indexingFeeAmount > 0)
                {
                    TransferDepositToken(new TransferInput
                    {
                        To = proposer,
                        Symbol = Context.Variables.NativeSymbol,
                        Amount = indexingFeeAmount,
                        Memo = "Index fee."
                    }, chainId);
                }

                if (arrearsAmount > 0)
                {
                    if (sideChainInfo.ArrearsInfo.TryGetValue(formattedProposerAddress, out var amount))
                    {
                        sideChainInfo.ArrearsInfo[formattedProposerAddress] = amount + arrearsAmount;
                    }
                    else
                        sideChainInfo.ArrearsInfo[formattedProposerAddress] = arrearsAmount;
                }

                State.SideChainInfo[chainId] = sideChainInfo;
                State.CurrentSideChainHeight[chainId] = currentSideChainHeight;
            }

            return indexedSideChainBlockData;
        }

        private void EnsureTransactionOnlyExecutedOnceInOneBlock()
        {
            Assert(State.LatestExecutedHeight.Value != Context.CurrentHeight, "Cannot execute this tx.");
            State.LatestExecutedHeight.Value = Context.CurrentHeight;
        }

        private Hash ConvertChainIdToHash(int chainId)
        {
            return HashHelper.ComputeFrom(chainId);
        }
    }
}