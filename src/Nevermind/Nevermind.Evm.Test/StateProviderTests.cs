﻿using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;
using NUnit.Framework;

namespace Nevermind.Evm.Test
{
    [TestFixture]
    public class StateProviderTests
    {
        private static readonly Keccak Hash1 = Keccak.Compute("1");
        private static readonly Keccak Hash2 = Keccak.Compute("2");

        private readonly Address _address1 = new Address(Hash1);
        private readonly Address _address2 = new Address(Hash2);

        [Test]
        public void Eip_158_zero_value_transfer_deletes()
        {
            StateTree tree = new StateTree(new InMemoryDb());
            StateProvider frontierProvider = new StateProvider(tree, new FrontierProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);
            frontierProvider.CreateAccount(_address1, 0);
            frontierProvider.Commit();
            StateProvider provider = new StateProvider(tree, new SpuriousDragonProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);
            provider.UpdateBalance(_address1, 0);
            provider.Commit();
            Assert.False(provider.AccountExists(_address1));
        }

        [Test]
        public void Empty_commit_restore()
        {
            StateProvider provider = new StateProvider(new StateTree(new InMemoryDb()), new FrontierProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);
            provider.Commit();
            provider.Restore(-1);
        }

        [Test]
        public void Is_empty_account()
        {
            StateProvider provider = new StateProvider(new StateTree(new InMemoryDb()), new FrontierProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);
            provider.CreateAccount(_address1, 0);
            provider.Commit();
            Assert.True(provider.IsEmptyAccount(_address1));
        }

        [Test]
        public void Restore_update_restore()
        {
            StateProvider provider = new StateProvider(new StateTree(new InMemoryDb()), new FrontierProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);
            provider.CreateAccount(_address1, 0);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.Restore(4);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.Restore(4);
            Assert.AreEqual(new BigInteger(4), provider.GetBalance(_address1));
        }

        [Test]
        public void Keep_in_cache()
        {
            StateProvider provider = new StateProvider(new StateTree(new InMemoryDb()), new FrontierProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);
            provider.CreateAccount(_address1, 0);
            provider.Commit();
            provider.GetBalance(_address1);
            provider.UpdateBalance(_address1, 1);
            provider.Restore(-1);
            provider.UpdateBalance(_address1, 1);
            provider.Restore(-1);
            provider.UpdateBalance(_address1, 1);
            provider.Restore(-1);
            Assert.AreEqual(new BigInteger(0), provider.GetBalance(_address1));
        }

        [Test]
        public void Restore_in_the_middle()
        {
            byte[] code = new byte[] {1};

            StateProvider provider = new StateProvider(new StateTree(new InMemoryDb()), new FrontierProtocolSpecification(), ShouldLog.State ? new ConsoleLogger() : null);
            provider.CreateAccount(_address1, 1);
            provider.UpdateBalance(_address1, 1);
            provider.IncrementNonce(_address1);
            Keccak codeHash = provider.UpdateCode(new byte[] { 1 });
            provider.UpdateCodeHash(_address1, codeHash);
            provider.UpdateStorageRoot(_address1, Hash2);

            Assert.AreEqual(BigInteger.One, provider.GetNonce(_address1));
            Assert.AreEqual(BigInteger.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(code, provider.GetCode(_address1));
            provider.Restore(4);
            Assert.AreEqual(BigInteger.One, provider.GetNonce(_address1));
            Assert.AreEqual(BigInteger.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(code, provider.GetCode(_address1));
            provider.Restore(3);
            Assert.AreEqual(BigInteger.One, provider.GetNonce(_address1));
            Assert.AreEqual(BigInteger.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(code, provider.GetCode(_address1));
            provider.Restore(2);
            Assert.AreEqual(BigInteger.One, provider.GetNonce(_address1));
            Assert.AreEqual(BigInteger.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(new byte[0], provider.GetCode(_address1));
            provider.Restore(1);
            Assert.AreEqual(BigInteger.Zero, provider.GetNonce(_address1));
            Assert.AreEqual(BigInteger.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(new byte[0], provider.GetCode(_address1));
            provider.Restore(0);
            Assert.AreEqual(BigInteger.Zero, provider.GetNonce(_address1));
            Assert.AreEqual(BigInteger.One, provider.GetBalance(_address1));
            Assert.AreEqual(new byte[0], provider.GetCode(_address1));
            provider.Restore(-1);
            Assert.AreEqual(false, provider.AccountExists(_address1));
        }
    }
}