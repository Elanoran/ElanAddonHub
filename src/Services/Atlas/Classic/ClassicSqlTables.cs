using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.Atlas.Classic
{
    // The tables (and per table the columns) the Classic baseline keeps. Column names are lower case. Everything else in the dump is skipped unread.
    static class ClassicSchema
    {
        public sealed class Spec
        {
            public string Name; public string[] Cols; public string[] Pk;
        }

        static Spec S(string name, string pk, string cols) => new Spec { Name = name, Pk = pk.Split(','), Cols = cols.Split(',') };

        const string LootCols = "entry,item,chanceorquestchance,groupid,mincountorref,maxcount,comments";
        const string VendorCols = "entry,item,maxcount,incrtime";
        const string SpawnCols = "guid,id,map,position_x,position_y,position_z,spawntimesecsmin,spawntimesecsmax";

        public static readonly Dictionary<string, Spec> Tables = new[]
        {
            S("creature_template", "entry", "entry,name,subname,minlevel,maxlevel,faction,family,creaturetype,npcflags,rank,lootid,pickpocketlootid,skinninglootid,vendortemplateid"),
            S("creature", "guid", SpawnCols),
            S("creature_spawn_entry", "guid,entry", "guid,entry"),
            S("creature_loot_template", "entry,item", LootCols),
            S("skinning_loot_template", "entry,item", LootCols),
            S("pickpocketing_loot_template", "entry,item", LootCols),
            S("reference_loot_template", "entry,item", LootCols),
            S("gameobject_loot_template", "entry,item", LootCols),
            S("gameobject_template", "entry", "entry,type,name,data0,data1"),
            S("gameobject", "guid", SpawnCols),
            S("gameobject_spawn_entry", "guid,entry", "guid,entry"),
            S("npc_vendor", "entry,item", VendorCols),
            S("npc_vendor_template", "entry,item", VendorCols),
            S("quest_template", "entry",
                "entry,title,minlevel,maxlevel,questlevel,zoneorsort,requiredraces,requiredclasses,srcitemid,srcitemcount,"
                + "prevquestid,nextquestid,exclusivegroup,breadcrumbforquestid,nextquestinchain,requiredskill,requiredskillvalue,"
                + "requiredminrepfaction,requiredminrepvalue,requiredmaxrepfaction,requiredmaxrepvalue,"
                + "reqitemid1,reqitemid2,reqitemid3,reqitemid4,reqitemcount1,reqitemcount2,reqitemcount3,reqitemcount4,"
                + "rewitemid1,rewitemid2,rewitemid3,rewitemid4,rewitemcount1,rewitemcount2,rewitemcount3,rewitemcount4,"
                + "rewchoiceitemid1,rewchoiceitemid2,rewchoiceitemid3,rewchoiceitemid4,rewchoiceitemid5,rewchoiceitemid6,"
                + "rewchoiceitemcount1,rewchoiceitemcount2,rewchoiceitemcount3,rewchoiceitemcount4,rewchoiceitemcount5,rewchoiceitemcount6"),
            S("creature_questrelation", "id,quest", "id,quest"),
            S("gameobject_questrelation", "id,quest", "id,quest"),
            S("creature_involvedrelation", "id,quest", "id,quest"),
            S("gameobject_involvedrelation", "id,quest", "id,quest"),
            S("item_template", "entry", "entry,name,quality,itemlevel,requiredlevel"),
        }.ToDictionary(x => x.Name);
    }

    // One kept table: rows hold only the kept columns (boxed long / double / string / null), plus a primary-key index for REPLACE / duplicate checks.
    sealed class ClassicSqlTable
    {
        public readonly ClassicSchema.Spec Spec;
        public readonly Dictionary<string, int> Idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly int[] PkIdx;
        public List<object[]> Rows = new List<object[]>();
        public readonly Dictionary<string, object[]> ByPk = new Dictionary<string, object[]>();
        public string[] AllCols;            // column order of the CREATE TABLE, for INSERTs without a column list
        public bool SchemaChanged;          // an ALTER TABLE ran: INSERTs without a column list can no longer be trusted
        public long Inserted, Replaced, Updated, Deleted, DuplicateSkipped;

        public ClassicSqlTable(ClassicSchema.Spec spec)
        {
            Spec = spec;
            for (int i = 0; i < spec.Cols.Length; i++) Idx[spec.Cols[i]] = i;
            PkIdx = spec.Pk.Select(p => Idx[p]).ToArray();
        }

        public string Key(object[] row)
        {
            if (PkIdx.Length == 1) return SqlVal.ToStr(row[PkIdx[0]]) ?? "";
            var sb = new System.Text.StringBuilder();
            foreach (var k in PkIdx) sb.Append(SqlVal.ToStr(row[k])).Append('|');
            return sb.ToString();
        }

        // false: key already present (INSERT without REPLACE)
        public bool Add(object[] row, bool replace)
        {
            var key = Key(row);
            if (ByPk.TryGetValue(key, out var old))
            {
                if (!replace) return false;
                int at = Rows.IndexOf(old);   // REPLACE is rare in the updates; an index scan is fine
                if (at >= 0) Rows[at] = row;
                ByPk[key] = row; Replaced++;
                return true;
            }
            ByPk[key] = row; Rows.Add(row); Inserted++;
            return true;
        }

        public int RemoveWhere(Func<object[], bool> match, int limit)
        {
            int n = 0;
            if (limit <= 0)
            {
                n = Rows.RemoveAll(r => { if (match(r)) { ByPk.Remove(Key(r)); return true; } return false; });
            }
            else
            {
                for (int i = 0; i < Rows.Count && n < limit; )
                    if (match(Rows[i])) { ByPk.Remove(Key(Rows[i])); Rows.RemoveAt(i); n++; } else i++;
            }
            Deleted += n;
            return n;
        }
    }

    // Applies the statements of a MySQL dump / update script to the kept tables, in memory. Understands CREATE TABLE (column order), INSERT / REPLACE
    // (multi-row, with or without a column list), UPDATE and DELETE with a WHERE of comparisons / IN / BETWEEN / LIKE / AND / OR / NOT, SET @var, and
    // arithmetic on constants, variables and columns. Everything else is counted and logged by reason, never fatal.
    public sealed class ClassicSqlExec
    {
        internal readonly Dictionary<string, ClassicSqlTable> Tables = new Dictionary<string, ClassicSqlTable>();
        readonly Dictionary<string, object> vars = new Dictionary<string, object>();

        public int? ContentVersion;                 // content_NNNN column of db_version: the update scripts numbered above it are not in the dump yet
        public long Applied, Ignored, Skipped;
        public readonly Dictionary<string, int> SkipReasons = new Dictionary<string, int>();
        public readonly List<string> SkipSamples = new List<string>();
        public readonly List<string> Notes = new List<string>();

        static readonly Regex ContentCol = new Regex(@"^content_(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public ClassicSqlExec()
        {
            foreach (var s in ClassicSchema.Tables.Values) Tables[s.Name] = new ClassicSqlTable(s);
        }

        public void ResetVars() { vars.Clear(); }

        // true: the statement is worth reading (decided from its first ~120 characters)
        public static bool WantHead(string head)
        {
            try
            {
                var lx = new ClassicSqlLexer(head);
                if (lx.IsWord("SET")) return true;
                if (lx.IsWord("INSERT") || lx.IsWord("REPLACE"))
                {
                    lx.Next();
                    while (lx.IsWord("LOW_PRIORITY") || lx.IsWord("DELAYED") || lx.IsWord("HIGH_PRIORITY") || lx.IsWord("IGNORE")) lx.Next();
                    lx.AcceptWord("INTO");
                    return Kept(lx);
                }
                if (lx.IsWord("UPDATE"))
                {
                    lx.Next();
                    while (lx.IsWord("LOW_PRIORITY") || lx.IsWord("IGNORE")) lx.Next();
                    return Kept(lx);
                }
                if (lx.IsWord("DELETE"))
                {
                    lx.Next();
                    while (lx.IsWord("LOW_PRIORITY") || lx.IsWord("QUICK") || lx.IsWord("IGNORE")) lx.Next();
                    if (!lx.AcceptWord("FROM")) return true;
                    return Kept(lx);
                }
                if (lx.IsWord("CREATE") || lx.IsWord("ALTER"))
                {
                    lx.Next();
                    if (!lx.AcceptWord("TABLE")) return false;
                    if (lx.AcceptWord("IF")) { lx.AcceptWord("NOT"); lx.AcceptWord("EXISTS"); }
                    return Kept(lx, true);
                }
                return false;
            }
            catch (ClassicSqlUnsupported) { return true; }
        }

        static bool Kept(ClassicSqlLexer lx, bool allowDbVersion = false)
        {
            if (lx.Kind != SqlTok.Ident) return true;
            var name = TableName(lx);
            return ClassicSchema.Tables.ContainsKey(name) || (allowDbVersion && name == "db_version");
        }

        static string TableName(ClassicSqlLexer lx)
        {
            if (lx.Kind != SqlTok.Ident) throw new ClassicSqlUnsupported("table name expected near '" + lx.Near() + "'");
            var n = lx.Text; lx.Next();
            while (lx.IsPunct("."))
            {
                lx.Next();
                if (lx.Kind != SqlTok.Ident) throw new ClassicSqlUnsupported("table name expected");
                n = lx.Text; lx.Next();
            }
            return n.ToLowerInvariant();
        }

        void Skip(string origin, string stmt, string reason)
        {
            Skipped++;
            var key = reason.Length > 80 ? reason.Substring(0, 80) : reason;
            SkipReasons[key] = SkipReasons.TryGetValue(key, out var n) ? n + 1 : 1;
            if (SkipSamples.Count < 40) SkipSamples.Add(origin + ": " + reason + " | " + (stmt.Length > 110 ? stmt.Substring(0, 110) + "..." : stmt));
        }

        public void Execute(string stmt, string origin)
        {
            ClassicSqlLexer lx;
            try
            {
                lx = new ClassicSqlLexer(stmt);
                if (lx.IsWord("INSERT")) { lx.Next(); Insert(lx, false, stmt, origin); }
                else if (lx.IsWord("REPLACE")) { lx.Next(); Insert(lx, true, stmt, origin); }
                else if (lx.IsWord("UPDATE")) { lx.Next(); Update(lx, stmt, origin); }
                else if (lx.IsWord("DELETE")) { lx.Next(); Delete(lx, stmt, origin); }
                else if (lx.IsWord("CREATE")) { lx.Next(); Create(lx, false); }
                else if (lx.IsWord("ALTER")) { lx.Next(); Create(lx, true); }
                else if (lx.IsWord("SET")) { lx.Next(); SetVars(lx); }
                else Ignored++;
            }
            catch (ClassicSqlUnsupported e) { Skip(origin, stmt, e.Message); }
            catch (Exception e) when (!(e is OutOfMemoryException))
            { Skip(origin, stmt, "error: " + e.Message); }
        }

        // ---- CREATE / ALTER TABLE
        void Create(ClassicSqlLexer lx, bool alter)
        {
            if (!lx.AcceptWord("TABLE")) { Ignored++; return; }
            if (lx.AcceptWord("IF")) { lx.AcceptWord("NOT"); lx.AcceptWord("EXISTS"); }
            var name = TableName(lx);
            if (alter)
            {
                if (Tables.TryGetValue(name, out var t0)) { t0.SchemaChanged = true; Notes.Add("ALTER TABLE " + name + " (INSERTs without a column list are skipped from here on)"); }
                else Ignored++;
                return;
            }
            bool isVersion = name == "db_version";
            if (!isVersion && !Tables.ContainsKey(name)) { Ignored++; return; }
            lx.ExpectPunct("(");
            var cols = new List<string>();
            while (true)
            {
                bool isCol = false; string cname = null;
                if (lx.Kind == SqlTok.Ident)
                {
                    bool keyword = !lx.Quoted && (lx.IsWord("PRIMARY") || lx.IsWord("KEY") || lx.IsWord("UNIQUE") || lx.IsWord("CONSTRAINT") || lx.IsWord("INDEX") || lx.IsWord("FULLTEXT") || lx.IsWord("FOREIGN") || lx.IsWord("CHECK") || lx.IsWord("SPATIAL"));
                    if (!keyword) { isCol = true; cname = lx.Text; }
                }
                int depth = 0;
                while (!lx.AtEnd)
                {
                    if (lx.IsPunct("(")) depth++;
                    else if (lx.IsPunct(")")) { if (depth == 0) break; depth--; }
                    else if (lx.IsPunct(",") && depth == 0) break;
                    lx.Next();
                }
                if (isCol) cols.Add(cname.ToLowerInvariant());
                if (lx.AtEnd || lx.IsPunct(")")) break;
                lx.Next();   // the comma
            }
            if (isVersion)
            {
                foreach (var c in cols)
                {
                    var m = ContentCol.Match(c);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && (ContentVersion == null || v > ContentVersion)) ContentVersion = v;
                }
                return;
            }
            var t = Tables[name];
            t.AllCols = cols.ToArray();
            t.SchemaChanged = false;
        }

        // ---- SET @a = expr [, @b := expr]
        void SetVars(ClassicSqlLexer lx)
        {
            if (lx.Kind != SqlTok.Var) { Ignored++; return; }
            while (lx.Kind == SqlTok.Var)
            {
                var name = lx.Text; lx.Next();
                if (!lx.AcceptPunct("=") && !lx.AcceptPunct(":=")) throw new ClassicSqlUnsupported("SET near '" + lx.Near() + "'");
                var probe = lx.Clone();
                SkipExpr(probe);
                try
                {
                    var ev = new ClassicSqlExpr(lx, null, null, vars).Parse();
                    vars[name] = ev(null);
                }
                catch (ClassicSqlUnsupported)
                {
                    vars[name] = ClassicSqlExpr.UnknownVar;
                    lx.CopyFrom(probe);
                }
                if (!lx.AcceptPunct(",")) break;
            }
            Applied++;
        }

        // moves past one expression without compiling it: stops at a top-level comma, WHERE, ORDER, LIMIT or the end
        static void SkipExpr(ClassicSqlLexer lx)
        {
            int depth = 0;
            while (!lx.AtEnd)
            {
                if (lx.IsPunct("(")) depth++;
                else if (lx.IsPunct(")")) { if (depth == 0) return; depth--; }
                else if (depth == 0 && (lx.IsPunct(",") || lx.IsWord("WHERE") || lx.IsWord("ORDER") || lx.IsWord("LIMIT"))) return;
                lx.Next();
            }
        }

        // ---- INSERT / REPLACE
        void Insert(ClassicSqlLexer lx, bool replace, string stmt, string origin)
        {
            bool ignore = false;
            while (lx.IsWord("LOW_PRIORITY") || lx.IsWord("DELAYED") || lx.IsWord("HIGH_PRIORITY")) lx.Next();
            if (lx.AcceptWord("IGNORE")) ignore = true;
            lx.AcceptWord("INTO");
            var name = TableName(lx);
            if (!Tables.TryGetValue(name, out var t)) { Ignored++; return; }

            string[] cols = null;
            if (lx.IsPunct("("))
            {
                var l = new List<string>();
                lx.Next();
                do
                {
                    if (lx.Kind != SqlTok.Ident) throw new ClassicSqlUnsupported("column list near '" + lx.Near() + "'");
                    l.Add(lx.Text.ToLowerInvariant()); lx.Next();
                } while (lx.AcceptPunct(","));
                lx.ExpectPunct(")");
                cols = l.ToArray();
            }
            if (!lx.AcceptWord("VALUES") && !lx.AcceptWord("VALUE")) throw new ClassicSqlUnsupported("INSERT form near '" + lx.Near() + "'");
            if (cols == null)
            {
                if (t.AllCols == null) throw new ClassicSqlUnsupported("INSERT without a column list and no CREATE TABLE seen for " + name);
                if (t.SchemaChanged) throw new ClassicSqlUnsupported("INSERT without a column list after ALTER TABLE " + name);
                cols = t.AllCols;
            }
            var map = new int[cols.Length];
            for (int i = 0; i < cols.Length; i++) map[i] = t.Idx.TryGetValue(cols[i], out var si) ? si : -1;

            var rows = new List<object[]>();
            do
            {
                lx.ExpectPunct("(");
                var row = new object[t.Spec.Cols.Length];
                int j = 0;
                if (!lx.IsPunct(")"))
                {
                    do
                    {
                        if (j >= map.Length) throw new ClassicSqlUnsupported("more values than columns");
                        if (map[j] < 0) { if (!lx.TrySkipLiteral()) SkipExpr(lx); }
                        else if (lx.TryLiteral(out var lit)) row[map[j]] = lit;
                        else row[map[j]] = new ClassicSqlExpr(lx, name, null, vars).Parse()(null);
                        j++;
                    } while (lx.AcceptPunct(","));
                }
                lx.ExpectPunct(")");
                if (j != map.Length) throw new ClassicSqlUnsupported("column count does not match value count");
                rows.Add(row);
            } while (lx.AcceptPunct(","));
            if (!lx.AtEnd) throw new ClassicSqlUnsupported("INSERT tail near '" + lx.Near() + "'");   // ON DUPLICATE KEY UPDATE, SELECT ...

            foreach (var row in rows)
            {
                if (!t.Add(row, replace))
                {
                    t.DuplicateSkipped++;
                    if (ignore) continue;
                    Skip(origin, stmt, "duplicate key in " + name);
                    return;
                }
            }
            Applied++;
        }

        // ---- UPDATE
        void Update(ClassicSqlLexer lx, string stmt, string origin)
        {
            while (lx.IsWord("LOW_PRIORITY") || lx.IsWord("IGNORE")) lx.Next();
            var name = TableName(lx);
            if (!Tables.TryGetValue(name, out var t)) { Ignored++; return; }
            lx.ExpectWord("SET");
            Func<string, int> col = c => t.Idx.TryGetValue(c, out var i) ? i : -1;
            var sets = new List<KeyValuePair<int, SqlEv>>();
            do
            {
                if (lx.Kind != SqlTok.Ident) throw new ClassicSqlUnsupported("SET near '" + lx.Near() + "'");
                var target = lx.Text; lx.Next();
                if (lx.IsPunct(".")) { lx.Next(); target = lx.Text; lx.Next(); }
                if (!lx.AcceptPunct("=")) throw new ClassicSqlUnsupported("SET near '" + lx.Near() + "'");
                int idx = col(target);
                if (idx < 0) SkipExpr(lx);                       // a column we do not keep: nothing to change
                else sets.Add(new KeyValuePair<int, SqlEv>(idx, new ClassicSqlExpr(lx, name, col, vars).Parse()));
            } while (lx.AcceptPunct(","));
            if (sets.Count == 0) { Applied++; return; }   // only columns we do not keep: nothing to do (the WHERE may use columns we do not keep, so it is not read)
            SqlEv where = null;
            if (lx.AcceptWord("WHERE")) where = new ClassicSqlExpr(lx, name, col, vars).Parse();
            int limit = 0;
            if (lx.IsWord("ORDER")) throw new ClassicSqlUnsupported("UPDATE ... ORDER BY");
            if (lx.AcceptWord("LIMIT")) { if (lx.Kind != SqlTok.Number) throw new ClassicSqlUnsupported("LIMIT"); limit = (int)lx.Int; lx.Next(); }
            if (!lx.AtEnd) throw new ClassicSqlUnsupported("UPDATE tail near '" + lx.Near() + "'");   // joins, sub-selects ...

            bool touchesKey = sets.Any(s => t.PkIdx.Contains(s.Key));
            int n = 0;
            foreach (var row in t.Rows)
            {
                if (where != null && SqlVal.Truth(where(row)) != true) continue;
                string oldKey = touchesKey ? t.Key(row) : null;
                foreach (var s in sets) row[s.Key] = s.Value(row);
                if (touchesKey)
                {
                    t.ByPk.Remove(oldKey);
                    t.ByPk[t.Key(row)] = row;
                }
                n++;
                if (limit > 0 && n >= limit) break;
            }
            t.Updated += n;
            Applied++;
        }

        // ---- DELETE
        void Delete(ClassicSqlLexer lx, string stmt, string origin)
        {
            while (lx.IsWord("LOW_PRIORITY") || lx.IsWord("QUICK") || lx.IsWord("IGNORE")) lx.Next();
            if (!lx.AcceptWord("FROM")) throw new ClassicSqlUnsupported("multi-table DELETE");
            var name = TableName(lx);
            if (!Tables.TryGetValue(name, out var t)) { Ignored++; return; }
            Func<string, int> col = c => t.Idx.TryGetValue(c, out var i) ? i : -1;
            SqlEv where = null;
            if (lx.AcceptWord("WHERE")) where = new ClassicSqlExpr(lx, name, col, vars).Parse();
            int limit = 0;
            if (lx.IsWord("ORDER")) throw new ClassicSqlUnsupported("DELETE ... ORDER BY");
            if (lx.AcceptWord("LIMIT")) { if (lx.Kind != SqlTok.Number) throw new ClassicSqlUnsupported("LIMIT"); limit = (int)lx.Int; lx.Next(); }
            if (!lx.AtEnd) throw new ClassicSqlUnsupported("DELETE tail near '" + lx.Near() + "'");
            t.RemoveWhere(r => where == null || SqlVal.Truth(where(r)) == true, limit);
            Applied++;
        }
    }
}
