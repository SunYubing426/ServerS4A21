using System;

namespace DfoServer.Network.Parsers.Pvp
{
    /// <summary>
    /// 2014 client payload for CMD 0x003B SET_PVP_MAP_INDEX.
    ///
    /// i16 mapIndex
    /// </summary>
    internal readonly struct SetPvpMapIndexRequest
    {
        private SetPvpMapIndexRequest(short mapIndex)
        {
            MapIndex = mapIndex;
        }

        internal short MapIndex { get; }

        internal static bool TryParse(
            byte[] body,
            out SetPvpMapIndexRequest request)
        {
            request = default;
            if (body == null || body.Length != 2)
                return false;

            request = new SetPvpMapIndexRequest(
                BitConverter.ToInt16(body, 0));
            return request.MapIndex >= 0;
        }
    }
}
