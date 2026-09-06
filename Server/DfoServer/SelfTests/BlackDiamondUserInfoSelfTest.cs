using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 由已注册的 premium-contract-protocol 专项及 --selftest-all 串行调用。
    internal static class BlackDiamondUserInfoSelfTest
    {
        internal static int RunChecks()
        {
            var failures = 0;
            void Check(string name, bool ok)
            {
                Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] black diamond: {name}");
                if (!ok) failures++;
            }

            // 不用 builder 的 offset 常量作为唯一 oracle；按客户端实际字段序跳过变长区。
            foreach (var petName in new[] { "", "宠物甲", "A" })
            {
                var tail = new UserInfoMinimumTailSnapshot
                {
                    EquippedCreatureItemId = petName.Length == 0 ? 0u : 123u,
                    EquippedCreatureNameBytes = ClientTextEncoding.GetBytes(petName),
                    EquippedCreatureAliveState = 1,
                    ExpertJobType = 1,
                    ExpertJobExp = 17,
                    MoodValue = 123,
                };
                var record = new CharacterRecord
                {
                    Name = ClientTextEncoding.GetBytes("资格测试"),
                    Subtype0Tail = tail,
                };
                var before = UserInfoSubtype0Builder.BuildNotificationBody(record);
                tail.BlackDiamondEligible = true;
                var after = UserInfoSubtype0Builder.BuildNotificationBody(record);
                var offset = FindAfterAlive(after);
                Check($"subtype0 pet='{petName}' changes only after-alive +1",
                    before.Length == after.Length
                    && after.Length - offset == 64
                    && after[offset] == 0 && after[offset + 1] == 1
                    && Enumerable.Range(0, after.Length)
                        .Where(i => after[i] != before[i]).SequenceEqual(new[] { offset + 1 }));
            }

            var roster0 = new GamePacketWriter();
            var roster1 = new GamePacketWriter();
            UserInfoType2RosterTailBuilder.WriteA21(roster0, 123);
            UserInfoType2RosterTailBuilder.WriteA21(roster1, 123, blackDiamondEligible: true);
            var plainRoster = roster0.ToArray();
            var memberRoster = roster1.ToArray();
            Check("subtype2 fixed 36B tail changes only +23, not premium-PC-room +22",
                plainRoster.Length == 36 && memberRoster.Length == 36
                && memberRoster[22] == 0 && memberRoster[23] == 1
                && Enumerable.Range(0, 36).Where(i => plainRoster[i] != memberRoster[i])
                    .SequenceEqual(new[] { 23 }));

            VerifyDatabaseCompatibility(Check);

            var path = Path.Combine(Path.GetTempPath(), "dfo-black-diamond-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id,m_id) VALUES (71,'diamond'),(72,'ordinary'),(73,'expired'),(74,'contract');
INSERT INTO characters(character_id,account_id,name) VALUES
    (7101,71,X'41'),(7102,71,X'42'),(7201,72,X'43'),(7301,73,X'44'),(7401,74,X'45');
INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES
    (71,56,@future),(73,56,1),(74,27,@future);
INSERT INTO character_subtype0_fields(character_id,is_premium_pc_room,server_group_id,stamina)
VALUES (7201,1,1,5);";
                    command.Parameters.AddWithValue("@future", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                    command.ExecuteNonQuery();
                }

                var repository = new SqliteSubtype0FieldsRepository(database);
                Check("account membership is shared by both characters",
                    repository.Load(7101).BlackDiamondEligible && repository.Load(7102).BlackDiamondEligible);
                Check("ordinary / expired / unrelated contract are not eligible",
                    !repository.Load(7201).BlackDiamondEligible
                    && !repository.Load(7301).BlackDiamondEligible
                    && !repository.Load(7401).BlackDiamondEligible);
                Check("character PC-room/server-group flags cannot grant black diamond",
                    repository.Load(7201).IsPremiumPcRoom == 1
                    && !repository.Load(7201).BlackDiamondEligible);
                Check("settlement and USERINFO use the same active-account policy",
                    PremiumService.HasActiveBlackDiamond(database.ConnectionString, 71)
                    && !PremiumService.HasActiveBlackDiamond(database.ConnectionString, 73));

                var characters = new SqliteCharacterRepository(database);
                var dataSource = new SqliteSelectCharacterDataSource(database, characters);
                var selected = dataSource.Load(7101, 71).CharacterRecord;
                var selectedBody = UserInfoSubtype0Builder.BuildNotificationBody(selected);
                Check("actual select-character data source carries membership into USERINFO",
                    selected.Subtype0Tail.BlackDiamondEligible
                    && selectedBody[FindAfterAlive(selectedBody) + 1] == 1);
                var roster = AccountCharacterListBodyBuilder.Build(
                    characters.ListByAccount(71), null, out _, accountId: 71, database: database);
                Check("actual account roster projects membership for each record",
                    ReadRosterEligibility(roster).SequenceEqual(new byte[] { 1, 1 }));

                var player = new PlayerContext { CharacterId = 7101 };
                var honor = new HonorLevelSyncService(characters, database);
                byte[] sent = null;
                bool Send()
                    => UserInfoBroadcastService.SendSubtype0Async(
                        player, 71, body => { sent = body; return Task.CompletedTask; },
                        characters, repository, honor, "black-diamond-selftest").GetAwaiter().GetResult();
                Check("town broadcast rebuilds and caches eligible subtype0",
                    Send() && sent[FindAfterAlive(sent) + 1] == 1 && player.Subtype0Tail.BlackDiamondEligible);

                var cached = repository.Load(7101);
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE account_premiums SET end_time=1 WHERE account_id=71;";
                    command.ExecuteNonQuery();
                }
                repository.RefreshDynamicTailFields(7101, cached);
                Check("expiry clears cached projection on refresh and both characters on reselect",
                    !cached.BlackDiamondEligible
                    && !repository.Load(7101).BlackDiamondEligible && !repository.Load(7102).BlackDiamondEligible);
                Check("town broadcast after expiry sends 0 rather than stale cached 1",
                    Send() && sent[FindAfterAlive(sent) + 1] == 0 && !player.Subtype0Tail.BlackDiamondEligible);
                Check("actual reselect reloads expired account membership",
                    !dataSource.Load(7101, 71).CharacterRecord.Subtype0Tail.BlackDiamondEligible);
                Check("expiry is also reflected in account roster",
                    ReadRosterEligibility(AccountCharacterListBodyBuilder.Build(
                        characters.ListByAccount(71), null, out _, accountId: 71, database: database))
                        .SequenceEqual(new byte[] { 0, 0 }));

                // 原错误兼容值必须失效；不能让狂热或霸王契约赋予黑钻资格。
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES(72,1,@future),(72,17,@future),(72,22,@future);";
                    command.Parameters.AddWithValue("@future", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                    command.ExecuteNonQuery();
                }
                Check("types 1/17/22 do not grant black diamond",
                    !repository.Load(7201).BlackDiamondEligible
                    && !PremiumService.HasActiveBlackDiamond(database.ConnectionString, 72));
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO account_premiums(account_id,premium_type,end_time) VALUES(72,56,@future);";
                    command.Parameters.AddWithValue("@future", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                    command.ExecuteNonQuery();
                }
                Check("native type 56 grants eligibility alongside independent contracts",
                    repository.Load(7201).BlackDiamondEligible
                    && PremiumService.HasActiveBlackDiamond(database.ConnectionString, 72));
                Check("projection does not alter unrelated stored weakness",
                    repository.Load(7201).Stamina == 5);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            return failures;
        }

        private static void VerifyDatabaseCompatibility(Action<string, bool> check)
        {
            foreach (var hasLegacyColumn in new[] { false, true })
            {
                var path = Path.Combine(Path.GetTempPath(),
                    "dfo-black-diamond-schema-" + Guid.NewGuid().ToString("N") + ".db");
                try
                {
                    var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                    using (var connection = database.OpenConnection())
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts(account_id,m_id) VALUES (71,'schema-fixture');
INSERT INTO characters(character_id,account_id,name) VALUES(7101,71,X'41');
INSERT INTO character_subtype0_fields(character_id,stamina_recover_end_unix) VALUES(7101,1234);
PRAGMA user_version=26;
UPDATE schema_metadata SET schema_version=26;";
                        command.ExecuteNonQuery();
                        if (!hasLegacyColumn)
                        {
                            command.CommandText = "ALTER TABLE character_subtype0_fields DROP COLUMN stamina_recover_end_unix;";
                            command.ExecuteNonQuery();
                        }
                    }
                    // Run the production migration entry twice; bootstrap caches
                    // already initialized paths within this test process.
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using var connection = database.OpenConnection();
                        Sqlite.SqliteMigrations.Apply(connection);
                        using var command = connection.CreateCommand();
                        command.CommandText = "PRAGMA user_version;";
                        var version = Convert.ToInt32(command.ExecuteScalar());
                        command.CommandText = "SELECT stamina_recover_end_unix FROM character_subtype0_fields WHERE character_id=7101;";
                        var deadline = Convert.ToInt64(command.ExecuteScalar());
                        command.CommandText = "SELECT schema_version FROM schema_metadata;";
                        var metadataVersion = Convert.ToInt32(command.ExecuteScalar());
                        check("v26 compatibility upgrade preserves data and is repeatable; legacy="
                            + hasLegacyColumn + " attempt=" + attempt,
                            version == 27 && metadataVersion == 27
                            && deadline == (hasLegacyColumn ? 1234 : 0));
                    }
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    foreach (var suffix in new[] { "", "-wal", "-shm" })
                        if (File.Exists(path + suffix)) File.Delete(path + suffix);
                }
            }
        }

        private static int FindAfterAlive(byte[] body)
        {
            using var reader = new BinaryReader(new MemoryStream(body));
            reader.BaseStream.Position = 43; // subtype/version/38B header/uid
            SkipDstr(reader);
            reader.BaseStream.Position += 6;
            var appearances = reader.ReadByte();
            reader.BaseStream.Position += appearances * 23 + 22; // appearance entries + fixed tail prefix
            reader.ReadUInt32(); // petId
            SkipDstr(reader);
            reader.ReadByte(); // alive
            return (int)reader.BaseStream.Position;
        }

        private static byte[] ReadRosterEligibility(byte[] body)
        {
            using var reader = new BinaryReader(new MemoryStream(body));
            reader.BaseStream.Position = 16;
            var count = reader.ReadUInt16();
            var result = new byte[count];
            for (var i = 0; i < count; i++)
            {
                reader.ReadUInt16(); // slot
                SkipDstr(reader);
                reader.BaseStream.Position += 15;
                var appearances = reader.ReadByte();
                reader.BaseStream.Position += appearances * 23;
                var tail = reader.ReadBytes(36);
                result[i] = tail[23];
            }
            if (reader.BaseStream.Position != body.Length)
                throw new InvalidDataException("A21 roster not consumed exactly");
            return result;
        }

        private static void SkipDstr(BinaryReader reader)
        {
            var length = reader.ReadUInt32();
            reader.BaseStream.Position += length;
        }
    }
}
