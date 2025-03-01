﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class BlockTreeTests
{
    private (BlockTree notSyncedTree, BlockTree syncedTree) BuildBlockTrees(
        int notSyncedTreeSize, int syncedTreeSize)
    {
        Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
        TestSpecProvider specProvider = new(London.Instance);
        specProvider.TerminalTotalDifficulty = 0;
        ;
        BlockTreeBuilder treeBuilder = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(notSyncedTreeSize);
        BlockTree notSyncedTree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);

        BlockTreeBuilder syncedTreeBuilder = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(syncedTreeSize);
        BlockTree syncedTree = new(
            syncedTreeBuilder.BlocksDb,
            syncedTreeBuilder.HeadersDb,
            syncedTreeBuilder.BlockInfoDb,
            syncedTreeBuilder.MetadataDb,
            syncedTreeBuilder.ChainLevelInfoRepository,
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);

        return (notSyncedTree, syncedTree);
    }

    [Test]
    public void Can_build_correct_block_tree()
    {
        Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
        TestSpecProvider specProvider = new(London.Instance);
        specProvider.TerminalTotalDifficulty = 0;
        BlockTreeBuilder treeBuilder = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(10);
        BlockTree tree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);

        Assert.AreEqual(9, tree.BestKnownNumber);
        Assert.AreEqual(9, tree.BestSuggestedBody!.Number);
        Assert.AreEqual(9, tree.Head!.Number);
    }

    [Test]
    public void Can_suggest_terminal_block_correctly()
    {
        // every block has difficulty 1000000, block 9 TD: 10000000
        TestSpecProvider specProvider = new(London.Instance);
        specProvider.TerminalTotalDifficulty = (UInt256)9999900;
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(10);
        BlockTree tree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), tree, specProvider, LimboLogs.Instance);

        Block? block8 = tree.FindBlock(8, BlockTreeLookupOptions.None);
        Assert.False(block8!.IsTerminalBlock(specProvider));
        Assert.AreEqual(9, tree.BestKnownNumber);
        Assert.AreEqual(9, tree.BestSuggestedBody!.Number);
        Assert.AreEqual(9, tree.Head!.Number);
        Assert.True(tree.Head.IsTerminalBlock(specProvider));
    }

    [Test]
    public void Suggest_terminal_block_with_lower_number_and_lower_total_difficulty()
    {
        TestSpecProvider specProvider = new(London.Instance);
        specProvider.TerminalTotalDifficulty = (UInt256)9999900;
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(10);
        BlockTree tree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), tree, specProvider, LimboLogs.Instance);

        Block? block7 = tree.FindBlock(7, BlockTreeLookupOptions.None);
        Block newTerminalBlock = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithParent(block7!.Header).TestObject)
            .WithParent(block7!)
            .WithTotalDifficulty((UInt256)9999950)
            .WithNumber(block7!.Number + 1).WithDifficulty(1999950).TestObject;
        // current Head TD: 10000000, block7 TD: 8000000, TTD 9999900, newTerminalBlock 9999950
        tree.SuggestBlock(newTerminalBlock);
        Assert.True(newTerminalBlock.IsTerminalBlock(specProvider));
        Assert.AreEqual(9, tree.BestKnownNumber);
        Assert.AreEqual(9, tree.BestSuggestedBody!.Number);
        Assert.AreEqual(9, tree.Head!.Number);
        Assert.True(tree.Head.IsTerminalBlock(specProvider));
    }

    [Test]
    public void Cannot_change_best_suggested_to_terminal_block_after_merge_block()
    {
        // every block has difficulty 1000000, block 9 TD: 10000000
        TestSpecProvider specProvider = new(London.Instance);
        specProvider.TerminalTotalDifficulty = (UInt256)9999900;
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(10);
        BlockTree tree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            specProvider,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), tree, specProvider, LimboLogs.Instance);

        Block? block8 = tree.FindBlock(8, BlockTreeLookupOptions.None);
        Assert.False(block8!.Header.IsTerminalBlock(specProvider));
        Assert.AreEqual(9, tree.BestKnownNumber);
        Assert.AreEqual(9, tree.BestSuggestedBody!.Number);
        Assert.AreEqual(9, tree.Head!.Number);
        Assert.True(tree.Head.IsTerminalBlock(specProvider));

        Block firstPoSBlock = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithParent(tree.Head!.Header).TestObject)
            .WithParent(tree.Head.Header)
            .WithDifficulty(0)
            .WithNumber(tree.Head!.Number + 1).TestObject;
        tree.SuggestBlock(firstPoSBlock);
        tree.UpdateMainChain(new[] { firstPoSBlock }, true, true); // simulating fcU
        Assert.AreEqual(10, tree.BestKnownNumber);
        Assert.AreEqual(10, tree.BestSuggestedBody!.Number);

        Block newTerminalBlock = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithParent(block8!.Header).TestObject)
            .WithParent(block8!)
            .WithTotalDifficulty((UInt256)10000001)
            .WithNumber(block8!.Number + 1).WithDifficulty(2000001).TestObject;
        Assert.True(newTerminalBlock.IsTerminalBlock(specProvider));
        tree.SuggestBlock(newTerminalBlock);
        Assert.AreEqual(10, tree.BestKnownNumber);
        Assert.AreEqual(10, tree.BestSuggestedBody!.Number);
    }

    [Test]
    public void Can_start_insert_pivot_block_with_correct_pointers()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertHeaderOptions insertHeaderOption = BlockTreeInsertHeaderOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, BlockTreeInsertBlockOptions.SaveHeader, insertHeaderOption);

        Assert.AreEqual(AddBlockResult.Added, insertResult);
        Assert.AreEqual(9, notSyncedTree.BestKnownNumber);
        Assert.AreEqual(9, notSyncedTree.BestSuggestedHeader!.Number);
        Assert.AreEqual(9, notSyncedTree.Head!.Number);
        Assert.AreEqual(9, notSyncedTree.BestSuggestedBody!.Number);
        Assert.AreEqual(14, notSyncedTree.BestKnownBeaconNumber);
        Assert.AreEqual(14, notSyncedTree.BestSuggestedBeaconHeader!.Number);
    }


    [Test]
    public void Can_insert_beacon_headers()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);

        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, BlockTreeInsertBlockOptions.SaveHeader, headerOptions);
        for (int i = 13; i > 9; --i)
        {
            BlockHeader? beaconHeader = syncedTree.FindHeader(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, headerOptions);
            Assert.AreEqual(insertOutcome, insertResult);
        }
    }

    [Test]
    public void Can_fill_beacon_headers_gap()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);

        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, BlockTreeInsertBlockOptions.SaveHeader, headerOptions);

        for (int i = 13; i > 9; --i)
        {
            BlockHeader? beaconHeader = syncedTree.FindHeader(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, headerOptions);
            Assert.AreEqual(AddBlockResult.Added, insertOutcome);
        }

        for (int i = 10; i < 14; ++i)
        {
            Block? block = syncedTree.FindBlock(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.SuggestBlock(block!);
            Assert.AreEqual(AddBlockResult.Added, insertOutcome);
        }

        Assert.AreEqual(13, notSyncedTree.BestSuggestedBody!.Number);
    }

    [Test]
    public void FindHeader_will_not_change_total_difficulty_when_it_is_zero()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);

        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, BlockTreeInsertBlockOptions.SaveHeader, headerOptions);
        BlockHeader? beaconHeader = syncedTree.FindHeader(13, BlockTreeLookupOptions.None);
        beaconHeader.TotalDifficulty = null;
        AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, headerOptions);
        Assert.AreEqual(insertOutcome, insertResult);

        BlockHeader? headerToCheck = notSyncedTree.FindHeader(beaconHeader.Hash, BlockTreeLookupOptions.None);
        Assert.IsNull(headerToCheck.TotalDifficulty);
    }

    [Test]
    public void FindBlock_will_not_change_total_difficulty_when_it_is_zero()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);

        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, BlockTreeInsertBlockOptions.SaveHeader, headerOptions);
        Block? beaconBlock2 = syncedTree.FindBlock(13, BlockTreeLookupOptions.None);
        beaconBlock2.Header.TotalDifficulty = null;
        AddBlockResult insertOutcome = notSyncedTree.Insert(beaconBlock2, BlockTreeInsertBlockOptions.None);
        Assert.AreEqual(insertOutcome, insertResult);

        Block? blockToCheck = notSyncedTree.FindBlock(beaconBlock2.Hash, BlockTreeLookupOptions.None);
        Assert.IsNull(blockToCheck.TotalDifficulty);
    }


    public static class BlockTreeTestScenario
    {
        public class ScenarioBuilder
        {
            private BlockTreeBuilder? _syncedTreeBuilder;
            private IChainLevelHelper? _chainLevelHelper;
            private IBeaconPivot _beaconPivot;

            public ScenarioBuilder WithBlockTrees(
                int notSyncedTreeSize,
                int syncedTreeSize = -1,
                bool moveBlocksToMainChain = true,
                UInt256? ttd = null,
                int splitVariant = 0,
                int splitFrom = 0,
                int syncedSplitVariant = 0,
                int syncedSplitFrom = 0
            ) {
                TestSpecProvider testSpecProvider = new TestSpecProvider(London.Instance);
                if (ttd != null) testSpecProvider.TerminalTotalDifficulty = ttd;
                NotSyncedTreeBuilder = Build.A.BlockTree().OfChainLength(notSyncedTreeSize, splitVariant: splitVariant, splitFrom: splitFrom);
                NotSyncedTree = new(
                    NotSyncedTreeBuilder.BlocksDb,
                    NotSyncedTreeBuilder.HeadersDb,
                    NotSyncedTreeBuilder.BlockInfoDb,
                    NotSyncedTreeBuilder.MetadataDb,
                    NotSyncedTreeBuilder.ChainLevelInfoRepository,
                    testSpecProvider,
                    NullBloomStorage.Instance,
                    new SyncConfig(),
                    LimboLogs.Instance);

                if (syncedTreeSize > 0)
                {
                    _syncedTreeBuilder = Build.A.BlockTree().OfChainLength(syncedTreeSize, splitVariant: syncedSplitVariant, splitFrom: syncedSplitFrom);
                    SyncedTree = new(
                        _syncedTreeBuilder.BlocksDb,
                        _syncedTreeBuilder.HeadersDb,
                        _syncedTreeBuilder.BlockInfoDb,
                        _syncedTreeBuilder.MetadataDb,
                        _syncedTreeBuilder.ChainLevelInfoRepository,
                        testSpecProvider,
                        NullBloomStorage.Instance,
                        new SyncConfig(),
                        LimboLogs.Instance);
                }

                _beaconPivot = new BeaconPivot(new SyncConfig(), new MemDb(), SyncedTree, LimboLogs.Instance);

                _chainLevelHelper = new ChainLevelHelper(NotSyncedTree, _beaconPivot, new SyncConfig(), LimboLogs.Instance);
                if (moveBlocksToMainChain)
                    NotSyncedTree.NewBestSuggestedBlock += OnNewBestSuggestedBlock;
                return this;
            }

            private void OnNewBestSuggestedBlock(object? sender, BlockEventArgs e)
            {
                NotSyncedTree.UpdateMainChain(new[] { e.Block! }, true);
            }

            public ScenarioBuilder InsertBeaconPivot(long num)
            {
                Block? beaconBlock = SyncedTree.FindBlock(num, BlockTreeLookupOptions.None);
                AddBlockResult insertResult = NotSyncedTree.Insert(beaconBlock!, BlockTreeInsertBlockOptions.SaveHeader,
                    BlockTreeInsertHeaderOptions.BeaconBlockInsert | BlockTreeInsertHeaderOptions.MoveToBeaconMainChain);
                Assert.AreEqual(AddBlockResult.Added, insertResult);
                NotSyncedTreeBuilder.MetadataDb.Set(MetadataDbKeys.LowestInsertedBeaconHeaderHash, Rlp.Encode(beaconBlock!.Hash).Bytes);
                NotSyncedTreeBuilder.MetadataDb.Set(MetadataDbKeys.BeaconSyncPivotNumber, Rlp.Encode(beaconBlock.Number).Bytes);
                return this;
            }

            public ScenarioBuilder ClearBeaconPivot()
            {
                NotSyncedTreeBuilder.MetadataDb.Delete(MetadataDbKeys.BeaconSyncPivotNumber);

                return this;
            }

            public ScenarioBuilder SuggestBlocks(long low, long high)
            {
                for (long i = low; i <= high; i++)
                {
                    Block? beaconBlock = SyncedTree!.FindBlock(i, BlockTreeLookupOptions.None);
                    AddBlockResult insertResult = NotSyncedTree!.SuggestBlock(beaconBlock!);
                    Assert.AreEqual(AddBlockResult.Added, insertResult);
                }

                return this;
            }

            public ScenarioBuilder SuggestBlocksUsingChainLevels(int maxCount = 2, long maxHeaderNumber = long.MaxValue)
            {
                BlockHeader[] headers = _chainLevelHelper!.GetNextHeaders(maxCount, maxHeaderNumber);
                while (headers != null && headers.Length > 1)
                {
                    BlockDownloadContext blockDownloadContext = new(
                        Substitute.For<ISpecProvider>(),
                        new PeerInfo(Substitute.For<ISyncPeer>()),
                        headers,
                        false,
                        Substitute.For<IReceiptsRecovery>()
                    );
                    bool shouldSetBlocks = NotSyncedTree.FindBlock(headers[1].Hash,
                        BlockTreeLookupOptions.TotalDifficultyNotNeeded) != null;
                    Assert.AreEqual(shouldSetBlocks, _chainLevelHelper.TrySetNextBlocks(maxCount, blockDownloadContext));
                    for (int i = 1; i < headers.Length; ++i)
                    {
                        Block? beaconBlock;
                        if (shouldSetBlocks)
                        {
                            beaconBlock = blockDownloadContext.Blocks[i - 1];
                        }
                        else
                        {
                            beaconBlock =
                                SyncedTree.FindBlock(headers[i].Hash!, BlockTreeLookupOptions.None);
                            beaconBlock.Header.TotalDifficulty = null;
                        }

                        AddBlockResult insertResult = NotSyncedTree.SuggestBlock(beaconBlock, BlockTreeSuggestOptions.ShouldProcess | BlockTreeSuggestOptions.FillBeaconBlock | BlockTreeSuggestOptions.ForceSetAsMain);
                        Assert.True(AddBlockResult.Added == insertResult, $"BeaconBlock {beaconBlock!.ToString(Block.Format.FullHashAndNumber)}");
                    }

                    headers = _chainLevelHelper!.GetNextHeaders(maxCount, maxHeaderNumber);
                }

                return this;
            }

            public enum TotalDifficultyMode
            {
                Null,
                Zero,
                TheSameAsSyncedTree
            }

            public ScenarioBuilder InsertBeaconHeaders(long low, long high, TotalDifficultyMode tdMode = TotalDifficultyMode.TheSameAsSyncedTree)
            {
                BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconHeaderInsert;
                if (tdMode == TotalDifficultyMode.Null)
                    headerOptions |= BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;
                for (long i = high; i >= low; --i)
                {
                    BlockHeader? beaconHeader = SyncedTree!.FindHeader(i, BlockTreeLookupOptions.None);

                    if (tdMode == TotalDifficultyMode.Null)
                        beaconHeader!.TotalDifficulty = null;
                    else if (tdMode == TotalDifficultyMode.Zero)
                        beaconHeader.TotalDifficulty = 0;
                    AddBlockResult insertResult = NotSyncedTree!.Insert(beaconHeader!, headerOptions);
                    Assert.AreEqual(AddBlockResult.Added, insertResult);
                }

                return this;
            }

            public ScenarioBuilder InsertBeaconBlocks(long low, long high, TotalDifficultyMode tdMode = TotalDifficultyMode.TheSameAsSyncedTree)
            {
                BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.BeaconBlockInsert | BlockTreeInsertHeaderOptions.MoveToBeaconMainChain;
                for (long i = high; i >= low; --i)
                {
                    Block? beaconBlock = SyncedTree!.FindBlock(i, BlockTreeLookupOptions.None);
                    if (tdMode == TotalDifficultyMode.Null)
                        beaconBlock!.Header.TotalDifficulty = null;
                    else if (tdMode == TotalDifficultyMode.Zero)
                        beaconBlock!.Header.TotalDifficulty = 0;

                    AddBlockResult insertResult = NotSyncedTree!.Insert(beaconBlock!, BlockTreeInsertBlockOptions.SaveHeader, insertHeaderOptions);
                    Assert.AreEqual(AddBlockResult.Added, insertResult);
                }

                return this;
            }

            public ScenarioBuilder InsertFork(long low, long high, bool moveToBeaconMainChain = false)
            {
                List<BlockInfo> blockInfos = new();
                List<Block> blocks = new List<Block>();
                Block? parent = null;
                for (long i = low; i <= high; i++)
                {
                    if (parent == null)
                        parent = SyncedTree.FindBlock(i - 1, BlockTreeLookupOptions.None)!;
                    Block blockToInsert = Build.A.Block.WithNumber(i).WithParent(parent).WithNonce(0).TestObject;
                    NotSyncedTree.Insert(blockToInsert, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.BeaconBlockInsert);
                    SyncedTree.Insert(blockToInsert, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.NotOnMainChain);

                    BlockInfo newBlockInfo = new(blockToInsert.Hash, UInt256.Zero, BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
                    newBlockInfo.BlockNumber = blockToInsert.Number;
                    blockInfos.Add(newBlockInfo);
                    blocks.Add(blockToInsert);
                    parent = blockToInsert;
                }

                if (moveToBeaconMainChain)
                {
                    SyncedTree.UpdateMainChain(blocks, true, true);
                    NotSyncedTree.UpdateBeaconMainChain(blockInfos.ToArray(), blockInfos[^1].BlockNumber);
                }

                return this;
            }

            public ScenarioBuilder InsertOtherChainToMain(BlockTree blockTree, long low, long high)
            {
                Block? parent = null;
                List<Block> newBlocks = new();
                for (long i = low; i <= high; i++)
                {
                    if (parent == null)
                        parent = blockTree.FindBlock(i - 1, BlockTreeLookupOptions.None)!;
                    Block blockToInsert = Build.A.Block.WithNumber(i).WithParent(parent).WithNonce(0).TestObject;
                    blockToInsert.Header.TotalDifficulty = parent.TotalDifficulty + blockToInsert.Difficulty;
                    blockTree.Insert(blockToInsert, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.BeaconBlockInsert);
                    newBlocks.Add(blockToInsert);

                    BlockInfo newBlockInfo = new(blockToInsert.Hash, UInt256.Zero, BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
                    newBlockInfo.BlockNumber = blockToInsert.Number;
                    parent = blockToInsert;
                }

                blockTree.UpdateMainChain(newBlocks, true, true);

                return this;
            }

            public ScenarioBuilder Restart()
            {
                NotSyncedTree = new(
                    NotSyncedTreeBuilder!.BlocksDb,
                    NotSyncedTreeBuilder.HeadersDb,
                    NotSyncedTreeBuilder.BlockInfoDb,
                    NotSyncedTreeBuilder.MetadataDb,
                    NotSyncedTreeBuilder.ChainLevelInfoRepository,
                    MainnetSpecProvider.Instance,
                    NullBloomStorage.Instance,
                    new SyncConfig(),
                    LimboLogs.Instance);
                _chainLevelHelper = new ChainLevelHelper(NotSyncedTree, _beaconPivot, new SyncConfig(), LimboLogs.Instance);
                return this;
            }

            public ScenarioBuilder AssertBestKnownNumber(long expected)
            {
                Assert.AreEqual(expected, NotSyncedTree!.BestKnownNumber);
                return this;
            }

            public ScenarioBuilder AssertBestSuggestedHeader(long expected)
            {
                Assert.AreEqual(expected, NotSyncedTree!.BestSuggestedHeader!.Number);
                return this;
            }

            public ScenarioBuilder AssertBestSuggestedBody(long expected, UInt256? expectedTotalDifficulty = null)
            {
                Assert.AreEqual(expected, NotSyncedTree!.BestSuggestedBody!.Number);
                if (expectedTotalDifficulty != null)
                    Assert.AreEqual(expectedTotalDifficulty, NotSyncedTree.BestSuggestedBody.TotalDifficulty);
                return this;
            }

            public ScenarioBuilder AssertMetadata(int startNumber, int finalNumber, BlockMetadata? metadata)
            {
                for (int i = startNumber; i < finalNumber; ++i)
                {
                    ChainLevelInfo? level = NotSyncedTree.FindLevel(i);
                    Assert.AreEqual(metadata, level?.BeaconMainChainBlock?.Metadata ?? BlockMetadata.None, $"Block number {i}");
                }

                return this;
            }

            public ScenarioBuilder AssertLowestInsertedBeaconHeader(long expected)
            {
                Assert.IsNotNull(NotSyncedTree);
                Assert.IsNotNull(NotSyncedTree!.LowestInsertedBeaconHeader);
                Assert.AreEqual(expected, NotSyncedTree!.LowestInsertedBeaconHeader!.Number);
                Console.WriteLine("LowestInsertedBeaconHeader:" + NotSyncedTree!.LowestInsertedBeaconHeader!.Number);
                return this;
            }

            public ScenarioBuilder InsertToHeaderDb(BlockHeader header)
            {
                HeaderDecoder headerDecoder = new();
                Rlp newRlp = headerDecoder.Encode(header);
                NotSyncedTreeBuilder.HeadersDb.Set(header.Hash!, newRlp.Bytes);
                return this;
            }

            public ScenarioBuilder InsertToBlockDb(Block block)
            {
                BlockDecoder blockDecoder = new();
                Rlp newRlp = blockDecoder.Encode(block);
                NotSyncedTreeBuilder.BlocksDb.Set(block.Hash, newRlp.Bytes);

                return this;
            }

            public ScenarioBuilder AssertBestBeaconHeader(long expected)
            {
                Assert.IsNotNull(NotSyncedTree);
                Assert.IsNotNull(NotSyncedTree.BestSuggestedBeaconHeader);
                Assert.AreEqual(expected, NotSyncedTree.BestSuggestedBeaconHeader?.Number);
                return this;
            }

            public ScenarioBuilder AssertBestBeaconBody(long expected)
            {
                Assert.IsNotNull(NotSyncedTree);
                Assert.IsNotNull(NotSyncedTree.BestSuggestedBeaconBody);
                Assert.AreEqual(expected, NotSyncedTree.BestSuggestedBeaconBody?.Number);
                return this;
            }

            public ScenarioBuilder AssertChainLevel(int startNumber, int finalNumber)
            {
                for (int i = startNumber; i < finalNumber; ++i)
                {
                    ChainLevelInfo? level = NotSyncedTree.FindLevel(i);
                    BlockInfo? blockInfo = level.MainChainBlock;
                    blockInfo.Should().NotBe(null, $"Current block number: {i}");
                    blockInfo.TotalDifficulty.Should().NotBe(0, $"Current block number: {i}");

                    ChainLevelInfo? syncedLevel = SyncedTree.FindLevel(i);
                    blockInfo.BlockHash.Should().Be(syncedLevel?.MainChainBlock.BlockHash, $"Current block number: {i}");
                }

                return this;
            }

            public ScenarioBuilder print()
            {
                // Console.WriteLine("LowestInsertedBeaconHeader:"+_notSyncedTree!.LowestInsertedBeaconHeader.Number);
                Console.WriteLine("Head:" + NotSyncedTree!.Head!.Number);
                Console.WriteLine("BestSuggestedHeader:" + NotSyncedTree!.BestSuggestedHeader.Number);
                Console.WriteLine("BestSuggestedBody:" + NotSyncedTree!.BestSuggestedBody.Number);
                // Console.WriteLine("LowestInsertedHeader:"+_notSyncedTree!.LowestInsertedHeader.Number);
                Console.WriteLine("BestKnownNumber:" + NotSyncedTree!.BestKnownNumber);
                Console.WriteLine("BestKnownBeaconNumber:" + NotSyncedTree!.BestKnownBeaconNumber);
                return this;
            }

            public BlockTree SyncedTree { get; private set; }

            public BlockTree NotSyncedTree { get; private set; }

            public BlockTreeBuilder NotSyncedTreeBuilder { get; private set; }
        }

        public static ScenarioBuilder GoesLikeThis()
        {
            return new();
        }
    }

    [Test]
    public void FindHeader_should_throw_exception_when_trying_to_find_dangling_block()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20);

        Block? beaconBlock = scenario.SyncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        scenario.InsertToHeaderDb(beaconBlock.Header);
        Assert.Throws<InvalidOperationException>(() => scenario.NotSyncedTree.FindHeader(beaconBlock.Header.Hash, BlockTreeLookupOptions.None));
    }

    [Test]
    public void FindBlock_should_throw_exception_when_trying_to_find_dangling_block()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20);

        Block? beaconBlock = scenario.SyncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        scenario.InsertToBlockDb(beaconBlock);
        Assert.Throws<InvalidOperationException>(() => scenario.NotSyncedTree.FindBlock(beaconBlock.Header.Hash, BlockTreeLookupOptions.None));
    }

    [Test]
    public void FindHeader_should_not_throw_exception_when_finding_blocks_with_known_beacon_info()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20)
            .InsertBeaconBlocks(18,19);

        Block? beaconBlock = scenario.SyncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        scenario.InsertToHeaderDb(beaconBlock.Header);
        Assert.DoesNotThrow(() => scenario.NotSyncedTree.FindHeader(beaconBlock.Header.Hash, BlockTreeLookupOptions.None));
    }

    [Test]
    public void FindBlock_should_not_throw_exception_when_finding_blocks_with_known_beacon_info()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20)
            .InsertBeaconBlocks(18,19);

        Block? beaconBlock = scenario.SyncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        scenario.InsertToBlockDb(beaconBlock);
        Assert.DoesNotThrow(() => scenario.NotSyncedTree.FindBlock(beaconBlock.Header.Hash, BlockTreeLookupOptions.None));
    }

    [Test]
    public void FindHeader_should_not_throw_exception_when_create_level_is_missing()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20);

        Block? beaconBlock = scenario.SyncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        scenario.InsertToHeaderDb(beaconBlock.Header);
        Assert.DoesNotThrow(() => scenario.NotSyncedTree.FindHeader(beaconBlock.Header.Hash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing));
    }

    [Test]
    public void FindBlock_should_not_throw_exception_when_create_level_is_missing()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20);

        Block? beaconBlock = scenario.SyncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        scenario.InsertToBlockDb(beaconBlock!);
        Assert.DoesNotThrow(() => scenario.NotSyncedTree.FindBlock(beaconBlock.Header.Hash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing));
    }

    [Test]
    public void Best_pointers_are_set_on_restart_with_gap()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20)
            .InsertBeaconPivot(14)
            .Restart()
            .AssertBestBeaconBody(14)
            .AssertBestBeaconHeader(14)
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9);
    }

    [Test]
    public void BeaconBlockInsert_does_not_change_best_blocks()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconBlocks(8, 9)
            .AssertBestSuggestedBody(3)
            .AssertBestSuggestedHeader(3)
            .AssertBestKnownNumber(3);
    }

    [Test]
    public void pointers_are_set_on_restart_during_header_sync()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(6, 6)
            .Restart()
            .AssertBestBeaconBody(7)
            .AssertBestBeaconHeader(7)
            .AssertLowestInsertedBeaconHeader(6)
            .AssertBestKnownNumber(3)
            .AssertBestSuggestedHeader(3)
            .AssertBestSuggestedBody(3);
    }

    [Test]
    public void pointers_are_set_on_restart_after_header_sync_finished()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .Restart()
            .AssertBestBeaconBody(7)
            .AssertBestBeaconHeader(7)
            .AssertLowestInsertedBeaconHeader(4)
            .AssertBestKnownNumber(3)
            .AssertBestSuggestedHeader(3)
            .AssertBestSuggestedBody(3);
    }

    [Test]
    public void pointers_are_set_on_restart_during_filling_block_gap()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 30)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 28)
            .SuggestBlocks(4, 25)
            .Restart()
            .AssertBestBeaconHeader(28)
            .AssertBestBeaconBody(28)
            .AssertLowestInsertedBeaconHeader(4)
            .AssertBestSuggestedHeader(25)
            .AssertBestSuggestedBody(25);
    }

    [Test]
    public void pointers_are_set_on_restart_after_filling_block_gap_finished()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .SuggestBlocks(4, 7)
            .ClearBeaconPivot()
            .Restart()
            .AssertBestBeaconBody(0)
            .AssertBestBeaconHeader(0)
            .AssertLowestInsertedBeaconHeader(4)
            .AssertBestSuggestedHeader(7)
            .AssertBestSuggestedBody(7)
            .AssertLowestInsertedBeaconHeader(4);
    }

    [Test]
    public void Best_pointers_should_not_move_if_sync_is_not_finished()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(5, 6)
            .InsertBeaconBlocks(8, 9)
            .Restart()
            .AssertBestBeaconBody(9)
            .AssertBestBeaconHeader(9)
            .AssertLowestInsertedBeaconHeader(5)
            .AssertBestKnownNumber(3)
            .AssertBestSuggestedHeader(3)
            .AssertBestSuggestedBody(3);
    }

    [Test]
    public void MarkChainAsProcessed_does_not_change_main_chain()
    {
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(9);
        BlockTree blockTree = new(
            blockTreeBuilder.BlocksDb,
            blockTreeBuilder.HeadersDb,
            blockTreeBuilder.BlockInfoDb,
            blockTreeBuilder.MetadataDb,
            blockTreeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        Block? parentBlock = blockTree.FindBlock(8, BlockTreeLookupOptions.None);
        Block newBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(parentBlock!.Header).TestObject).TestObject;
        AddBlockResult addBlockResult = blockTree.SuggestBlock(newBlock);
        Assert.AreEqual(AddBlockResult.Added, addBlockResult);
        blockTree.MarkChainAsProcessed(new[] { newBlock });
        Assert.False(blockTree.IsMainChain(newBlock.Header));
    }

    [Test]
    public void Fork_do_not_change_beacon_main_chain_block()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconBlocks(5, 9);

        ChainLevelInfo? level6 = scenario.NotSyncedTree.FindLevel(6);
        Keccak previousBlockHash = level6.BeaconMainChainBlock.BlockHash;

        scenario.InsertFork(6, 8);
        level6 = scenario.NotSyncedTree.FindLevel(6);
        level6.BlockInfos.Length.Should().Be(2);
        level6.BeaconMainChainBlock.BlockHash.Should().Be(previousBlockHash);
    }

    [Test]
    public void Can_reorg_beacon_main_chain()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconBlocks(4, 9)
            .InsertFork(6, 9, true)
            .SuggestBlocksUsingChainLevels()
            .AssertChainLevel(0, 9);
    }

    [Test]
    public void Can_set_total_difficulty_when_suggested_with_0()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconBlocks(8, 9, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Zero);

        Block? block = scenario.NotSyncedTree.FindBlock(8, BlockTreeLookupOptions.None);
        AddBlockResult result = scenario.NotSyncedTree.SuggestBlock(block);
        result.Should().Be(AddBlockResult.Added);
        scenario.NotSyncedTree.FindBlock(8, BlockTreeLookupOptions.None).TotalDifficulty.Should().NotBe((UInt256)0);
    }
}
