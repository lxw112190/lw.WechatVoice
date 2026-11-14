using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
internal static class LibraryCheck
{
    // Compares the C# library/speech_cache port (src/VoiceEngine) against the
    // Python reference on fixtures built by tests/library_fixtures.py: index
    // content via the API surface, filters, sorting (incl. transcript cache
    // join), payload, filenames and cache semantics. Synthetic data only.
    private static Type library, speechCache;
    private static MethodInfo build, counting, listing, records, payload, filename, wavFilename;
    private static MethodInfo cacheLookup, cacheSave, cacheAnnotate, cacheRead;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Invoke(MethodInfo method, params object[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException exc) { throw exc.InnerException; }
    }
    private static bool Rejects(MethodInfo method, string fragment, params object[] args)
    {
        try { Invoke(method, args); return false; }
        catch (InvalidDataException exc) { return exc.Message.Contains(fragment); }
        catch { return false; }
    }
    private static string Json(string root) { return File.ReadAllText(Path.Combine(root, "expected.json")); }
    private static Dictionary<string, object> Parse(string json) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
    private static List<object> ArrayOf(object value)
    {
        var enumerable = value as System.Collections.IEnumerable;
        return enumerable == null ? new List<object>() : enumerable.Cast<object>().ToList();
    }
    private static Dictionary<string, object> RowOf(object value) { return (Dictionary<string, object>)value; }
    private static string ToJson(object value) { return new JavaScriptSerializer().Serialize(value); }
    private static string Hex(byte[] value)
    {
        var chars = new char[value.Length * 2];
        for (int i = 0; i < value.Length; i++)
        {
            int high = value[i] >> 4, low = value[i] & 15;
            chars[i * 2] = (char)(high < 10 ? '0' + high : 'a' + high - 10);
            chars[i * 2 + 1] = (char)(low < 10 ? '0' + low : 'a' + low - 10);
        }
        return new string(chars);
    }
    private static List<Dictionary<string, object>> Rows(string indexPath, Dictionary<string, object> request, long? page)
    {
        return (List<Dictionary<string, object>>)Invoke(records, indexPath, request, page);
    }
    private static List<string> Ids(List<Dictionary<string, object>> rows) { return rows.Select(r => Convert.ToString(r["id"])).ToList(); }

    private static void CompareRows(List<object> expected, List<Dictionary<string, object>> actual, string context)
    {
        Require(expected.Count == actual.Count, context + " row count differs: " + actual.Count + " vs " + expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var want = (System.Collections.ArrayList)expected[i];
            var got = actual[i];
            string[] keys = { "id", "db", "rid", "chat", "label", "local_id", "stamp", "server_id", "sender", "bytes" };
            for (int k = 0; k < keys.Length && k < want.Count; k++)
                Require(Convert.ToString(want[k]) == Convert.ToString(got[keys[k]]),
                    context + " row " + i + " field " + keys[k] + " differs: " + Convert.ToString(got[keys[k]]) + " vs " + Convert.ToString(want[k]));
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.WriteLine("usage: LibraryCheck.exe <VoiceEngine.exe> <library-fixtures-dir>"); return 1; }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        library = assembly.GetType("VoiceEngine.Library", true);
        speechCache = assembly.GetType("VoiceEngine.SpeechCache", true);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        build = library.GetMethod("Build", flags);
        counting = library.GetMethod("Counting", flags);
        listing = library.GetMethod("Listing", flags);
        records = library.GetMethod("Records", flags);
        payload = library.GetMethod("Payload", flags);
        filename = library.GetMethod("Filename", flags);
        wavFilename = library.GetMethod("WavFilename", flags);
        cacheLookup = speechCache.GetMethod("Lookup", flags);
        cacheSave = speechCache.GetMethod("Save", flags);
        cacheAnnotate = speechCache.GetMethod("Annotate", flags);
        cacheRead = speechCache.GetMethod("Read", flags);
        string root = Path.GetFullPath(args[1]);
        string storage = Path.Combine(root, "db_storage");
        string index = Path.Combine(root, "cs-index.sqlite");
        File.Delete(index);
        try
        {
            var expected = Parse(Json(root));
            var expectedReport = RowOf(expected["report"]);

            // Build
            var report = (Dictionary<string, object>)Invoke(build, storage, index, new Action(() => { }), new Action<Dictionary<string, object>>(value => { }));
            Require(Convert.ToInt64(report["count"]) == Convert.ToInt64(expectedReport["count"]), "build count differs");
            Require(Convert.ToInt64(report["message_voices"]) == Convert.ToInt64(expectedReport["message_voices"]), "build message_voices differs");
            var wantIssues = new HashSet<string>(ArrayOf(expectedReport["issues"]).Select(Convert.ToString));
            var gotIssues = new HashSet<string>(ArrayOf(report["issues"]).Select(Convert.ToString));
            Require(wantIssues.SetEquals(gotIssues), "build issues differ: [" + string.Join(";", gotIssues) + "] vs [" + string.Join(";", wantIssues) + "]");

            // voices rows through the API surface (compare by id order on both sides)
            var wantVoices = ArrayOf(expected["voices"]).Cast<System.Collections.ArrayList>()
                .OrderBy(w => Convert.ToString(w[0])).Cast<object>().ToList();
            var gotVoices = Rows(index, new Dictionary<string, object>(), 0L)
                .OrderBy(r => Convert.ToString(r["id"])).Cast<Dictionary<string, object>>().ToList();
            CompareRows(wantVoices, gotVoices, "voices");

            // counting
            Require(Convert.ToInt64(Invoke(counting, index, new Dictionary<string, object> { ["chat"] = "chat" })) == Convert.ToInt64(expected["count_chat"]), "chat count differs");
            Require(Convert.ToInt64(Invoke(counting, index, new Dictionary<string, object>())) == Convert.ToInt64(expected["count_all"]), "total count differs");

            // filters
            var filtered = RowOf(expected["filtered"]);
            Require(Ids(Rows(index, new Dictionary<string, object> { ["after"] = 1700000010L, ["before"] = 1700000025L }, 0L)).SequenceEqual(ArrayOf(filtered["dates"]).Select(Convert.ToString)), "date filter differs");
            var twoIds = ArrayOf(filtered["ids"]).Select(Convert.ToString).ToList();
            Require(Ids(Rows(index, new Dictionary<string, object> { ["ids"] = twoIds }, 0L)).SequenceEqual(twoIds), "ids filter differs");
            Require(Convert.ToInt64(Invoke(counting, index, new Dictionary<string, object> { ["ids"] = new List<string>() })) == Convert.ToInt64(filtered["empty_ids_count"]), "empty ids must count zero");
            Require(Rejects(counting, "开始日期", index, new Dictionary<string, object> { ["after"] = 9L, ["before"] = 8L }), "reversed dates not rejected");
            Require(Rejects(counting, "无效", index, new Dictionary<string, object> { ["ids"] = new List<string> { "not-hex" } }), "bad ids not rejected");
            Require(Rejects(records, "排序条件", index, new Dictionary<string, object> { ["sort"] = "evil;drop", ["descending"] = false }, 0L), "bad sort not rejected");

            // sorts without cache
            var sorted = RowOf(expected["sorted"]);
            foreach (string column in new[] { "stamp", "sender", "label", "bytes", "local_id" })
                foreach (string direction in new[] { "asc", "desc" })
                {
                    var request = new Dictionary<string, object> { ["sort"] = column, ["descending"] = direction == "desc" };
                    Require(Ids(Rows(index, request, 0L)).SequenceEqual(ArrayOf(sorted[column + "_" + direction]).Select(Convert.ToString)), "sort " + column + " " + direction + " differs");
                }

            // cache: the Python-generated transcripts.sqlite already sits next to
            // the index (cache path = index.parent/transcripts.sqlite), so both
            // implementations share it directly; then roundtrip one extra save.
            var firstRow = Rows(index, new Dictionary<string, object>(), 0L)[0];
            byte[] payloadBytes = (byte[])Invoke(payload, storage, firstRow);
            string digest;
            using (var sha = SHA256.Create()) digest = Hex(sha.ComputeHash(payloadBytes));
            Require(Hex(payloadBytes) == Convert.ToString(expected["payload"]), "payload bytes differ");
            var cache = RowOf(expected["cache"]);
            var hit = (Dictionary<string, object>)Invoke(cacheLookup, index, Convert.ToString(firstRow["id"]), digest);
            Require(hit != null && Convert.ToString(hit["text"]) == Convert.ToString(cache["lookup_hit"]), "cache lookup hit differs");
            Require(Invoke(cacheLookup, index, Convert.ToString(firstRow["id"]), new string('f', 64)) == null, "cache digest mismatch reused");
            var thirdId = Convert.ToString(Ids(Rows(index, new Dictionary<string, object>(), 0L))[2]);
            var errorHit = Invoke(cacheLookup, index, thirdId, new string('e', 64));
            Require(errorHit == null, "ERROR entries must not satisfy lookup");
            Invoke(cacheSave, index, "zzzzzzzzzzzzzzzzzzzzzzzz", digest, "READY", "新增条目");
            var saved = (Dictionary<string, object>)Invoke(cacheLookup, index, "zzzzzzzzzzzzzzzzzzzzzzzz", digest);
            Require(saved != null && Convert.ToString(saved["text"]) == "新增条目", "cache save/lookup roundtrip failed");
            Require(Rejects(cacheSave, "格式无效", index, "zzzzzzzzzzzzzzzzzzzzzzzz", digest, "BOGUS", "x"), "invalid cache status accepted");
            Require(Rejects(cacheRead, "一页", index, Enumerable.Range(0, 51).Select(i => "aaaaaaaaaaaaaaaaaaaaaaaa").ToList()), "cache read page limit not enforced");

            // cache-backed sorting and annotated items
            foreach (string direction in new[] { "asc", "desc" })
            {
                var request = new Dictionary<string, object> { ["sort"] = "recognition_status", ["descending"] = direction == "desc" };
                Require(Ids(Rows(index, request, 0L)).SequenceEqual(ArrayOf(sorted["status_" + direction]).Select(Convert.ToString)), "status sort " + direction + " differs");
            }
            Require(Ids(Rows(index, new Dictionary<string, object> { ["sort"] = "transcript", ["descending"] = false }, 0L))
                .SequenceEqual(ArrayOf(sorted["transcript_asc"]).Select(Convert.ToString)), "transcript sort differs");
            var listingResult = (Dictionary<string, object>)Invoke(listing, index, new Dictionary<string, object> { ["include_contacts"] = true, ["page"] = 0L });
            var expectedListing = RowOf(expected["listing"]);
            Require(Convert.ToInt64(listingResult["count"]) == Convert.ToInt64(expectedListing["count"]), "listing count differs");
            Require(!Convert.ToBoolean(listingResult["cache_warning"]), "cache warning with healthy cache");
            // expected listing items were recorded before the cache was written;
            // cache_items were recorded after, so they carry transcripts/statuses.
            var wantItems = ArrayOf(expected["cache_items"]).Cast<Dictionary<string, object>>().ToList();
            var gotItems = ((List<Dictionary<string, object>>)listingResult["items"]);
            Require(wantItems.Count == gotItems.Count, "listing item count differs");
            for (int i = 0; i < wantItems.Count; i++)
                foreach (string key in new[] { "id", "transcript", "recognition_status", "sender" })
                    Require(Convert.ToString(wantItems[i][key]) == Convert.ToString(gotItems[i].ContainsKey(key) ? gotItems[i][key] : ""), "listing item " + i + " " + key + " differs");
            var wantContacts = ArrayOf(expectedListing["contacts"]).Cast<Dictionary<string, object>>().ToList();
            var gotContacts = ((List<Dictionary<string, object>>)listingResult["contacts"]);
            Require(wantContacts.Count == gotContacts.Count, "listing contacts count differs");
            for (int i = 0; i < wantContacts.Count; i++)
                foreach (string key in new[] { "user", "label", "count", "latest_stamp" })
                    Require(Convert.ToString(wantContacts[i][key]) == Convert.ToString(gotContacts[i][key]), "contact " + i + " " + key + " differs");

            // corrupt cache: listing still works, warning surfaces
            File.WriteAllBytes(Path.Combine(root, "transcripts.sqlite"), new byte[] { 1, 2, 3, 4 });
            var warned = (Dictionary<string, object>)Invoke(listing, index, new Dictionary<string, object> { ["include_contacts"] = true, ["page"] = 0L });
            Require(Convert.ToBoolean(warned["cache_warning"]), "corrupt cache did not warn");
            var noCrash = Rows(index, new Dictionary<string, object> { ["sort"] = "transcript", ["descending"] = false }, 0L);
            Require(noCrash.Count == Convert.ToInt32(expectedListing["count"]), "corrupt cache broke voice listing");

            // filenames
            Require(Convert.ToString(Invoke(filename, firstRow)) == Convert.ToString(expected["filename"]), "silk filename differs");
            string messy = "  今天<|zh|>一起??出去**走走  <|en|>abc  ";
            Require(Convert.ToString(Invoke(wavFilename, firstRow, messy)) == Convert.ToString(expected["wav_filename"]), "wav filename differs");
            Require(Convert.ToString(Invoke(wavFilename, firstRow, "")) == Convert.ToString(expected["wav_filename_empty"]), "empty text filename differs");

            File.WriteAllText(Path.Combine(root, "library-check.txt"), "PASS");
            Console.WriteLine("PASS: C# library/speech_cache port matches Python reference: build, voices, filters, all sorts, cache join, payload, filenames.");
            return 0;
        }
        catch (Exception exc)
        {
            Console.WriteLine("FAIL: harness error: " + exc);
            return 1;
        }
    }
}
