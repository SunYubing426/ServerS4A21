using System;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    public static class SelectCharacterFatigueAckSelfTest
    {
        public static int Run()
        {
            var snapshot = new SelectCharacterDataSnapshot
            {
                CharacterRecord = new CharacterRecord
                {
                    CharacterId = 0,
                    CreatedAt = DateTime.UnixEpoch,
                    Level = 20,
                },
                InitializationSnapshot = new SelectCharacterInitializationSnapshot
                {
                    AckFatigueUsed = 44,
                    AckFatigueLimit = 188,
                },
            };

            var built = SelectCharacterAckBodyBuilder.TryBuild(snapshot, out var body);
            var passed = built
                && body.Length >= 17
                && BitConverter.ToUInt16(body, 11) == 44
                && BitConverter.ToUInt16(body, 13) == 188
                && BitConverter.ToUInt16(body, 15) == 0;

            Console.WriteLine(passed
                ? "SELECT_CHARACTER fatigue ACK selftest PASS"
                : "SELECT_CHARACTER fatigue ACK selftest FAIL");
            return passed ? 0 : 1;
        }
    }
}
