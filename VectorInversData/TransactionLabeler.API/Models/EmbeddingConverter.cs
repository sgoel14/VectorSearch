using System;

namespace TransactionLabeler.API.Models
{
    public static class EmbeddingConverter
    {
        public static byte[] ToBytes(float[] floats)
        {
            if (floats == null) return null;
            var bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float[] ToFloats(byte[] bytes)
        {
            if (bytes == null) return null;
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
    }
} 