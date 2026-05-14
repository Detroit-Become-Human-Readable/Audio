using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Audio.Parser
{
    public enum WwiseAudioType
    {
        Sfx,
        Music,
        Voice,
        Unknown
    }

    internal sealed class WwiseObject
    {
        public byte Type { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }

    public static class WwiseAudioTypeDetector
    {
        private static readonly HashSet<byte> MusicObjectTypes = new() { 10, 11, 12, 13 };
        private static readonly HashSet<byte> TraversableObjectTypes = new() { 3, 4, 5, 7, 10, 12, 13 };

        public static IReadOnlyDictionary<uint, WwiseAudioType> DetectBankMediaTypes(string bankPath)
        {
            if (!File.Exists(bankPath))
            {
                return new Dictionary<uint, WwiseAudioType>();
            }

            byte[] bank = File.ReadAllBytes(bankPath);
            List<uint> mediaIds = new();
            Dictionary<uint, WwiseObject> objects = new();

            int position = 0;
            while (position + 8 <= bank.Length)
            {
                string chunkName = System.Text.Encoding.ASCII.GetString(bank, position, 4);
                uint chunkSize = BitConverter.ToUInt32(bank, position + 4);
                int payloadOffset = position + 8;
                long nextPosition = (long)payloadOffset + chunkSize;
                if (nextPosition > bank.Length)
                {
                    break;
                }

                if (chunkName == "DIDX")
                {
                    for (int offset = payloadOffset; offset + 12 <= nextPosition; offset += 12)
                    {
                        mediaIds.Add(BitConverter.ToUInt32(bank, offset));
                    }
                }
                else if (chunkName == "HIRC")
                {
                    objects = ParseHircObjects(bank, payloadOffset, (int)chunkSize);
                }

                position = (int)nextPosition;
            }

            Dictionary<uint, WwiseAudioType> result = mediaIds.Distinct().ToDictionary(id => id, _ => WwiseAudioType.Unknown);
            if (result.Count == 0 || objects.Count == 0)
            {
                return result;
            }

            HashSet<uint> knownMediaIds = result.Keys.ToHashSet();
            HashSet<uint> knownObjectIds = objects.Keys.ToHashSet();
            Dictionary<uint, HashSet<byte>> mediaObjectTypes = new();
            Dictionary<uint, HashSet<byte>> memo = new();

            foreach (var (objectId, obj) in objects)
            {
                HashSet<byte> resolvedTypes = ResolveObjectMediaTypes(objectId, objects, knownObjectIds, knownMediaIds, memo, new HashSet<uint>());
                foreach (uint mediaId in FindMediaIdsInObject(obj, knownMediaIds))
                {
                    AddObjectTypes(mediaObjectTypes, mediaId, resolvedTypes.Count > 0 ? resolvedTypes : new HashSet<byte> { obj.Type });
                }

                foreach (uint mediaId in ResolveObjectMediaIds(objectId, objects, knownObjectIds, knownMediaIds, new Dictionary<uint, HashSet<uint>>(), new HashSet<uint>()))
                {
                    AddObjectTypes(mediaObjectTypes, mediaId, resolvedTypes.Count > 0 ? resolvedTypes : new HashSet<byte> { obj.Type });
                }
            }

            foreach (uint mediaId in knownMediaIds)
            {
                if (!mediaObjectTypes.TryGetValue(mediaId, out HashSet<byte>? objectTypes))
                {
                    continue;
                }
                result[mediaId] = ClassifyObjectTypes(objectTypes);
            }

            return result;
        }

        public static string FolderName(WwiseAudioType type)
        {
            return type switch
            {
                WwiseAudioType.Music => "MUS",
                WwiseAudioType.Voice => "VO",
                WwiseAudioType.Sfx => "SFX",
                _ => "UNKNOWN"
            };
        }

        private static Dictionary<uint, WwiseObject> ParseHircObjects(byte[] bank, int payloadOffset, int chunkSize)
        {
            Dictionary<uint, WwiseObject> objects = new();
            if (chunkSize < 4 || payloadOffset + 4 > bank.Length)
            {
                return objects;
            }

            uint count = BitConverter.ToUInt32(bank, payloadOffset);
            int cursor = payloadOffset + 4;
            int end = Math.Min(bank.Length, payloadOffset + chunkSize);
            for (uint index = 0; index < count && cursor + 9 <= end; index++)
            {
                byte type = bank[cursor];
                uint size = BitConverter.ToUInt32(bank, cursor + 1);
                uint objectId = BitConverter.ToUInt32(bank, cursor + 5);
                int payloadStart = cursor + 9;
                int payloadLength = (int)Math.Max(0, size - 4);
                if (payloadStart + payloadLength > end)
                {
                    break;
                }

                byte[] payload = new byte[payloadLength];
                Buffer.BlockCopy(bank, payloadStart, payload, 0, payloadLength);
                objects[objectId] = new WwiseObject { Type = type, Payload = payload };
                cursor = payloadStart + payloadLength;
            }
            return objects;
        }

        private static HashSet<uint> ResolveObjectMediaIds(
            uint objectId,
            IReadOnlyDictionary<uint, WwiseObject> objects,
            HashSet<uint> knownObjectIds,
            HashSet<uint> knownMediaIds,
            Dictionary<uint, HashSet<uint>> memo,
            HashSet<uint> stack)
        {
            if (memo.TryGetValue(objectId, out HashSet<uint>? cached))
            {
                return new HashSet<uint>(cached);
            }
            if (!objects.TryGetValue(objectId, out WwiseObject? obj) || !stack.Add(objectId))
            {
                return new HashSet<uint>();
            }

            HashSet<uint> result = FindMediaIdsInObject(obj, knownMediaIds);
            if (TraversableObjectTypes.Contains(obj.Type))
            {
                foreach (uint childId in FindUInt32Values(obj.Payload, knownObjectIds))
                {
                    foreach (uint mediaId in ResolveObjectMediaIds(childId, objects, knownObjectIds, knownMediaIds, memo, stack))
                    {
                        result.Add(mediaId);
                    }
                }
            }

            stack.Remove(objectId);
            memo[objectId] = new HashSet<uint>(result);
            return result;
        }

        private static HashSet<byte> ResolveObjectMediaTypes(
            uint objectId,
            IReadOnlyDictionary<uint, WwiseObject> objects,
            HashSet<uint> knownObjectIds,
            HashSet<uint> knownMediaIds,
            Dictionary<uint, HashSet<byte>> memo,
            HashSet<uint> stack)
        {
            if (memo.TryGetValue(objectId, out HashSet<byte>? cached))
            {
                return new HashSet<byte>(cached);
            }
            if (!objects.TryGetValue(objectId, out WwiseObject? obj) || !stack.Add(objectId))
            {
                return new HashSet<byte>();
            }

            HashSet<byte> result = new();
            if (FindMediaIdsInObject(obj, knownMediaIds).Count > 0)
            {
                result.Add(obj.Type);
            }
            if (TraversableObjectTypes.Contains(obj.Type))
            {
                foreach (uint childId in FindUInt32Values(obj.Payload, knownObjectIds))
                {
                    foreach (byte childType in ResolveObjectMediaTypes(childId, objects, knownObjectIds, knownMediaIds, memo, stack))
                    {
                        result.Add(childType);
                    }
                }
            }

            stack.Remove(objectId);
            memo[objectId] = new HashSet<byte>(result);
            return result;
        }

        private static HashSet<uint> FindMediaIdsInObject(WwiseObject obj, HashSet<uint> knownMediaIds)
        {
            HashSet<uint> hits = new();
            if (obj.Type == 2 && obj.Payload.Length >= 9)
            {
                uint primary = BitConverter.ToUInt32(obj.Payload, 5);
                if (knownMediaIds.Contains(primary))
                {
                    hits.Add(primary);
                }
            }
            if (obj.Type == 11)
            {
                foreach (int offset in new[] { 10, 27 })
                {
                    if (obj.Payload.Length >= offset + 4)
                    {
                        uint mediaId = BitConverter.ToUInt32(obj.Payload, offset);
                        if (knownMediaIds.Contains(mediaId))
                        {
                            hits.Add(mediaId);
                        }
                    }
                }
            }

            if (hits.Count == 0 && (obj.Type == 2 || MusicObjectTypes.Contains(obj.Type)))
            {
                hits.UnionWith(FindUInt32Values(obj.Payload, knownMediaIds));
            }
            return hits;
        }

        private static HashSet<uint> FindUInt32Values(byte[] payload, HashSet<uint> allowedValues)
        {
            HashSet<uint> hits = new();
            for (int offset = 0; offset + 4 <= payload.Length; offset++)
            {
                uint value = BitConverter.ToUInt32(payload, offset);
                if (allowedValues.Contains(value))
                {
                    hits.Add(value);
                }
            }
            return hits;
        }

        private static void AddObjectTypes(Dictionary<uint, HashSet<byte>> target, uint mediaId, HashSet<byte> objectTypes)
        {
            if (!target.TryGetValue(mediaId, out HashSet<byte>? bucket))
            {
                bucket = new HashSet<byte>();
                target[mediaId] = bucket;
            }
            foreach (byte objectType in objectTypes)
            {
                bucket.Add(objectType);
            }
        }

        private static WwiseAudioType ClassifyObjectTypes(HashSet<byte> objectTypes)
        {
            if (objectTypes.Contains(11) || objectTypes.Any(type => MusicObjectTypes.Contains(type)))
            {
                return WwiseAudioType.Music;
            }
            if (objectTypes.Contains(15))
            {
                return WwiseAudioType.Voice;
            }
            if (objectTypes.Contains(2) || objectTypes.Overlaps(new[] { (byte)3, (byte)4, (byte)5, (byte)7 }))
            {
                return WwiseAudioType.Sfx;
            }
            return WwiseAudioType.Unknown;
        }
    }
}
