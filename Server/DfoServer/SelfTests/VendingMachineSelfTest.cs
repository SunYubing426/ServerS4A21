using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Premium;
using DfoServer.Game.VendingMachine;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class VendingMachineSelfTest
    {
        public static int Run()
        {
            try
            {
                Require(VendingMachineUseRequest.TryParse(Convert.FromHexString("03000000010000007C00"), out var advanced)
                    && advanced.MachineId == 3 && advanced.GroupId == 1 && advanced.SourceSlot == 124, "captured advanced request");
                Require(VendingMachineUseRequest.TryParse(Convert.FromHexString("01000000010000007B00"), out var normal)
                    && normal.MachineId == 1 && normal.SourceSlot == 123, "captured normal request");
                Require(!VendingMachineUseRequest.TryParse(new byte[9], out _)
                    && !VendingMachineUseRequest.TryParse(new byte[11], out _)
                    && !VendingMachineUseRequest.TryParse(Convert.FromHexString("02000000010000007B00"), out _)
                    && !VendingMachineUseRequest.TryParse(Convert.FromHexString("0100000001000000FFFF"), out _), "reject malformed or unsupported request");

                var catalog = VendingMachineCatalog.Load();
                Require(catalog.TryGet(1, 1, out var normalGroup) && normalGroup.MaterialId == 7454
                    && normalGroup.MaterialCount == 1, "normal PVF material");
                Require(catalog.TryGet(3, 1, out var advancedGroup) && advancedGroup.MaterialId == 2749933
                    && advancedGroup.MaterialCount == 1 && !catalog.TryGet(1, 2, out _), "advanced PVF material and group isolation");
                foreach (var group in new[] { normalGroup, advancedGroup })
                {
                    var ticket = 0;
                    foreach (var reward in group.Rewards.Where(r => r.Weight > 0))
                    {
                        Require(group.Roll(ticket) == reward && group.Roll(ticket + reward.Weight - 1) == reward,
                            $"weighted boundaries {group.MaterialId}/{reward.ItemId}x{reward.Count}");
                        Require(InventoryRewardGrantService.TryCreateOnly(reward.ItemId, ItemCreateReason.PackageOpen,
                            reward.Count, out var created) && created.GrantedCount == reward.Count,
                            $"PVF reward creation {reward.ItemId}x{reward.Count}");
                        ticket += reward.Weight;
                    }
                    Require(ticket == group.TotalWeight, "entire positive probability mass covered");
                }

                using (var f = new Fixture())
                {
                    f.Token(7454, 3);
                    f.Token(2749933, 2, 124);
                    var service = f.Service();
                    Require(!service.TryUse(f.Lease, Guid.NewGuid(), 7, 1, 1, 123, out _)
                        && !service.TryUse(f.Lease, f.SessionId, 8, 1, 1, 123, out _)
                        && !f.Use(service, 3, out _), "wrong owner/account/material cannot consume");
                    Require(f.Use(service, 1, out var result) && result.Reward.ItemId == 7463
                        && result.Reward.Count == 4 && result.RemainingTokens == 2 && !result.DeliveredToMailbox,
                        "normal draw commits exact token and reward");
                    var packet = VendingMachineResultBuilder.Build(result);
                    Require(packet.Length == 127 && packet[0] == 1 && BitConverter.ToInt16(packet, 1) == 123
                        && BitConverter.ToInt32(packet, 3) == 2 && BitConverter.ToInt32(packet, 7) == 7463
                        && BitConverter.ToInt32(packet, 11) == 4 && BitConverter.ToInt32(packet, 15) == 0
                        && BitConverter.ToInt32(packet, 19) == 0 && BitConverter.ToUInt16(packet, 23) == 1
                        && packet[25] == 0 && BitConverter.ToInt32(packet, 28) == 7463
                        && BitConverter.ToInt32(packet, 32) == 4, "A21 callback consumes 25B header plus type and 101B entry");
                    using var read = f.Database.OpenConnection();
                    var persisted = InventoryService.LoadFromDb(read, 11, 7, f.Database);
                    Require(persisted.GetItem(InventoryListType.Main, 123).Count == 2
                        && persisted.GetItem(InventoryListType.Main, result.MainEntries[0].slot).Count == 4, "reward and cost survive reload");
                    Require(f.Use(service, 1, out var second) && second.RemainingTokens == 1
                        && second.MainEntries[0].core.Count == 8, "repeated draw pays again and merges stack");
                    Require(f.Use(service, 1, out var last) && last.RemainingTokens == 0
                        && !f.Use(service, 1, out _), "last token removes source and empty source cannot draw");
                    Require(f.Use(service, 3, out var high, 124) && high.Reward.Count == 5 && high.RemainingTokens == 1,
                        "advanced machine uses its own token and reward count");
                }

                using (var f = new Fixture())
                {
                    f.Token(7454, 2);
                    f.Execute("UPDATE account_premiums SET end_time=0;");
                    Require(!f.Use(f.Service(), 1, out _) && f.Count() == 2, "expired black diamond cannot draw");
                    f.Execute("UPDATE account_premiums SET end_time=4102444800;");
                    var expired = f.Lease.Inventory.GetItem(InventoryListType.Main, 123).Copy();
                    expired.ExpireTime = 1;
                    f.Lease.Inventory.SetItem(InventoryListType.Main, 123, expired);
                    Require(!f.Use(f.Service(), 1, out _) && f.Count() == 2, "expired token cannot draw");
                }

                using (var f = new Fixture())
                {
                    f.Token(7454, 2);
                    f.FillConsumables();
                    Require(f.Use(f.Service(), 1, out var mail) && mail.DeliveredToMailbox && mail.RemainingTokens == 1
                        && mail.MainEntries.Count == 0 && VendingMachineResultBuilder.Build(mail).Length == 25,
                        "full bag gets mailbox and independent reward icon header");
                    var attachment = f.Mailbox.LoadInbox(11, 10).Single().Attachments.Single();
                    Require(attachment.ItemTemplateId == 7463 && attachment.ItemCount == 4, "mail attachment is exact reward");
                }

                using (var f = new Fixture())
                {
                    f.Token(7454, 2);
                    f.FillConsumables();
                    Require(!f.Use(f.Service(rejectMail: true), 1, out _) && f.Count() == 2
                        && f.Mailbox.LoadInbox(11, 10).Count == 0, "mail failure restores cost and prior dirty bag");
                }

                var premiumTicket = 0;
                VendingReward premiumReward = null;
                foreach (var reward in normalGroup.Rewards)
                {
                    if (reward.Weight > 0 && PremiumService.IsContractItem(reward.ItemId)) { premiumReward = reward; break; }
                    premiumTicket += reward.Weight;
                }
                Require(premiumReward != null, "real PVF contains contract rewards");
                using (var f = new Fixture())
                {
                    f.Token(7454, 2);
                    Require(f.Use(f.Service(premiumTicket), 1, out var contract) && contract.PremiumType > 0
                        && contract.PremiumRemaining > 0 && contract.MainEntries.Count == 0
                        && PremiumService.HasActivePremium(f.Database.ConnectionString, 7, contract.PremiumType),
                        "contract activated in token transaction");
                    var before = f.PremiumExpiry(contract.PremiumType);
                    Require(f.Use(f.Service(premiumTicket), 1, out var extended)
                        && f.PremiumExpiry(contract.PremiumType) > before && extended.RemainingTokens == 0,
                        "existing contract is extended once per paid draw");
                }

                // Fail persistence after reward/mail/Premium application: all writes must roll back.
                foreach (var mode in new[] { "inventory", "mail", "premium" })
                using (var f = new Fixture())
                {
                    f.Token(7454, 2);
                    if (mode == "mail") f.FillConsumables();
                    Require(InventoryPersistenceService.SaveDirty(f.Lease), "seed durable failure fixture");
                    f.Execute("CREATE TRIGGER fail_vending BEFORE UPDATE ON character_inventory_items WHEN NEW.slot_index=123 BEGIN SELECT RAISE(ABORT,'injected vending persistence failure'); END;");
                    var service = f.Service(mode == "premium" ? premiumTicket : 0);
                    Require(!f.Use(service, 1, out _) && f.Count() == 2
                        && f.Mailbox.LoadInbox(11, 10).Count == 0, mode + " failure rolls back token and mailbox");
                    if (mode == "premium")
                    {
                        PremiumService.TryResolveContractItem(premiumReward.ItemId, out var type, out _);
                        Require(f.PremiumExpiry(type) == 0, "failed draw does not activate contract");
                    }
                    else if (mode == "inventory")
                        Require(f.Lease.Inventory.GetItem(InventoryListType.Main, 3) == null
                            && f.Lease.Inventory.GetItem(InventoryListType.Main, 65) == null, "failed draw restores empty reward slot");
                    f.Execute("DROP TRIGGER fail_vending;");
                }

                using (var f = new Fixture())
                {
                    f.Token(7454, 1);
                    var stale = f.Lease;
                    InventoryContext.Unregister(f.SessionId);
                    Require(!f.Service().TryUse(stale, f.SessionId, 7, 1, 1, 123, out _), "retired inventory lease cannot draw");
                }
                Console.WriteLine("VENDING_MACHINE selftest passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("VENDING_MACHINE selftest failed: " + ex);
                return 1;
            }
        }

        private static void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "a21-vending-" + Guid.NewGuid().ToString("N"));
            internal Guid SessionId { get; } = Guid.NewGuid();
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }
            internal MailboxService Mailbox { get; }
            internal Fixture()
            {
                Directory.CreateDirectory(_directory);
                Database = new GameDatabase(Path.Combine(_directory, "test.db"), ServerPaths.SchemaFilePath);
                Execute("INSERT INTO accounts(account_id,m_id) VALUES(7,'vending-test'); INSERT INTO characters(character_id,account_id,name) VALUES(11,7,'vending-test'); INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES(7,12,4102444800);");
                using var connection = Database.OpenConnection();
                Lease = InventoryContext.Register(SessionId, InventoryService.LoadFromDb(connection, 11, 7, Database));
                Mailbox = new MailboxService(new MailboxRepository(Database));
            }
            internal VendingMachineService Service(int ticket = 0, bool rejectMail = false) => new(
                rejectMail ? RejectingInventoryOverflowRewardSink.Instance : new MailboxInventoryOverflowRewardSink(Mailbox),
                random: _ => ticket);
            internal bool Use(VendingMachineService service, int machine, out VendingMachineResult result, short slot = 123) =>
                service.TryUse(Lease, SessionId, 7, machine, 1, slot, out result);
            internal void Token(int id, int count, short slot = 123) => Put(id, count, slot);
            private void Put(int id, int count, short slot)
            {
                Require(InventoryRewardGrantService.TryCreateOnly(id, ItemCreateReason.AdminGrant, count, out var created)
                    && Lease.Inventory.SetItem(InventoryListType.Main, slot, created.Core), "fixture item " + id);
            }
            internal int Count() => Lease.Inventory.GetItem(InventoryListType.Main, 123)?.Count ?? 0;
            internal void FillConsumables()
            {
                for (short slot = 3; slot <= 8; slot++) Put(7464, 1, slot);
                for (short slot = 65; slot <= 120; slot++) Put(7464, 1, slot);
            }
            internal void Execute(string sql)
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            internal long PremiumExpiry(int type)
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT end_time FROM account_premiums WHERE account_id=7 AND premium_type=@type";
                command.Parameters.AddWithValue("@type", type);
                return Convert.ToInt64(command.ExecuteScalar());
            }
            public void Dispose()
            {
                InventoryContext.Unregister(SessionId);
                SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(_directory);
                if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(resolved).StartsWith("a21-vending-", StringComparison.Ordinal))
                    Directory.Delete(resolved, true);
            }
        }
    }
}


