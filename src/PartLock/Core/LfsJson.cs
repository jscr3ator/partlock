// PartLock - parsing for `git lfs locks --json` output (both the --verify shape and the plain array shape).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PartLock.Core
{
    [DataContract]
    public class LfsLockOwner
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    /// <summary>One lock exactly as git-lfs serializes it.</summary>
    [DataContract]
    public class LfsLockDto
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "path")]
        public string Path { get; set; }

        [DataMember(Name = "owner")]
        public LfsLockOwner Owner { get; set; }

        /// <summary>ISO-8601 string; kept as text because DataContractJsonSerializer won't parse git-lfs dates.</summary>
        [DataMember(Name = "locked_at")]
        public string LockedAt { get; set; }
    }

    /// <summary>`git lfs locks --verify --json` returns {"ours":[...],"theirs":[...]}.</summary>
    [DataContract]
    public class LfsLocksEnvelope
    {
        [DataMember(Name = "ours")]
        public List<LfsLockDto> Ours { get; set; }

        [DataMember(Name = "theirs")]
        public List<LfsLockDto> Theirs { get; set; }

        [DataMember(Name = "locks")]
        public List<LfsLockDto> Locks { get; set; }
    }

    /// <summary>Parsed locks, already split into mine / other people's.</summary>
    public class LfsLockSet
    {
        public LfsLockSet()
        {
            Ours = new List<LockInfo>();
            Theirs = new List<LockInfo>();
        }

        /// <summary>True when the server told us which locks are ours (--verify), so no name guessing is needed.</summary>
        public bool VerifyFormat { get; set; }

        public List<LockInfo> Ours { get; private set; }
        public List<LockInfo> Theirs { get; private set; }

        /// <summary>Owner name taken from the first of our locks; used to learn the server account name.</summary>
        public string FirstOursOwner { get; set; }

        public List<LockInfo> All()
        {
            List<LockInfo> all = new List<LockInfo>(Ours.Count + Theirs.Count);
            all.AddRange(Ours);
            all.AddRange(Theirs);
            return all;
        }
    }

    public static class LfsJson
    {
        /// <summary>
        /// Parses either {"ours":[],"theirs":[]} (or {"locks":[]}) or a bare array of locks.
        /// In array form locks are attributed to the current user by owner name (case-insensitive).
        /// </summary>
        public static LfsLockSet Parse(string json, string currentUserName)
        {
            LfsLockSet result = new LfsLockSet();
            if (string.IsNullOrEmpty(json)) return result;

            string text = json.Trim();
            if (text.Length == 0) return result;

            if (text[0] == '[')
            {
                List<LfsLockDto> list = Deserialize<List<LfsLockDto>>(text);
                if (list == null) return result;

                result.VerifyFormat = false;
                foreach (LfsLockDto dto in list)
                {
                    if (dto == null) continue;
                    LockInfo info = ToLockInfo(dto);
                    if (IsSameUser(info.Owner, currentUserName))
                    {
                        info.IsMine = true;
                        result.Ours.Add(info);
                    }
                    else
                    {
                        result.Theirs.Add(info);
                    }
                }
                return result;
            }

            LfsLocksEnvelope envelope = Deserialize<LfsLocksEnvelope>(text);
            if (envelope == null) return result;

            if (envelope.Ours == null && envelope.Theirs == null)
            {
                // Some git-lfs versions emit {"locks":[...]} for plain `locks --json`.
                if (envelope.Locks == null) return result;
                result.VerifyFormat = false;
                foreach (LfsLockDto dto in envelope.Locks)
                {
                    if (dto == null) continue;
                    LockInfo info = ToLockInfo(dto);
                    if (IsSameUser(info.Owner, currentUserName))
                    {
                        info.IsMine = true;
                        result.Ours.Add(info);
                    }
                    else
                    {
                        result.Theirs.Add(info);
                    }
                }
                return result;
            }

            result.VerifyFormat = true;

            if (envelope.Ours != null)
            {
                foreach (LfsLockDto dto in envelope.Ours)
                {
                    if (dto == null) continue;
                    LockInfo info = ToLockInfo(dto);
                    info.IsMine = true;
                    if (string.IsNullOrEmpty(result.FirstOursOwner) && !string.IsNullOrEmpty(info.Owner))
                        result.FirstOursOwner = info.Owner;
                    result.Ours.Add(info);
                }
            }

            if (envelope.Theirs != null)
            {
                foreach (LfsLockDto dto in envelope.Theirs)
                {
                    if (dto == null) continue;
                    LockInfo info = ToLockInfo(dto);
                    info.IsMine = false;
                    result.Theirs.Add(info);
                }
            }

            return result;
        }

        /// <summary>Parses lock output without a known server account name.</summary>
        public static LfsLockSet Parse(string json)
        {
            return Parse(json, null);
        }

        /// <summary>Parses the single object printed by `git lfs lock --json`.</summary>
        public static LockInfo ParseSingle(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string text = json.Trim();
            if (text.Length == 0 || text[0] != '{') return null;

            LfsLockDto dto = Deserialize<LfsLockDto>(text);
            return dto == null ? null : ToLockInfo(dto);
        }

        /// <summary>Parses the single lock object printed by `git lfs lock --json`. Returns null on bad input.</summary>
        public static LfsLockDto ParseSingleLock(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            string text = json.Trim();
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1).TrimStart();

            try
            {
                if (text.StartsWith("{", StringComparison.Ordinal))
                    return Deserialize<LfsLockDto>(text);

                if (text.StartsWith("[", StringComparison.Ordinal))
                {
                    List<LfsLockDto> list = Deserialize<List<LfsLockDto>>(text);
                    return list != null && list.Count > 0 ? list[0] : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        /// <summary>Reads an ISO-8601 timestamp and returns it in UTC.</summary>
        public static DateTime ParseTimestamp(string text)
        {
            return ParseDate(text);
        }

        public static LockInfo ToLockInfo(LfsLockDto dto)
        {
            LockInfo info = new LockInfo();
            info.Id = dto.Id;
            info.Path = NormalizePath(dto.Path);
            info.Owner = dto.Owner == null ? null : dto.Owner.Name;
            info.LockedAtUtc = ParseDate(dto.LockedAt);
            return info;
        }

        public static string NormalizePath(string path)
        {
            if (path == null) return string.Empty;
            return path.Replace('\\', '/');
        }

        private static bool IsSameUser(string owner, string userName)
        {
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(userName)) return false;
            return string.Equals(owner.Trim(), userName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime ParseDate(string text)
        {
            if (string.IsNullOrEmpty(text)) return DateTime.MinValue;
            DateTime value;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
            {
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
            return DateTime.MinValue;
        }

        private static T Deserialize<T>(string json) where T : class
        {
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return serializer.ReadObject(stream) as T;
                }
            }
            catch (SerializationException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (Exception)
            {
                // Malformed JSON surfaces as XmlException/SerializationException/etc. depending on runtime.
                return null;
            }
        }
    }
}
