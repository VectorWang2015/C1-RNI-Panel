using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace RniPanel {
    public sealed class FilmStyle {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Uuid { get; set; }
        public string Icc { get; set; }
        public int Strength { get; set; }
        public bool IccExists { get; set; }
        // Native C1 tree path, including the RNI root. Unlike Name this remains
        // distinct for standard/grain editions which share the same label.
        public string[] NativePath { get; set; }
    }
    public sealed class FilmFamily {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Folder { get; set; }
        public bool Grain { get; set; }
        public bool Rendered { get; set; }
        public List<FilmStyle> Styles = new List<FilmStyle>();
        public FilmStyle At(int strength) { return Styles.FirstOrDefault(s => s.Strength == strength); }
        public bool Matches(string query) {
            return Catalog.Normalize(Name + Category + (Grain ? " grain 颗粒" : " standard 标准") + (Rendered ? " jpg tiff" : " raw"))
                .Contains(Catalog.Normalize(query));
        }
    }
    public sealed class StyleCatalog {
        public List<FilmFamily> Families = new List<FilmFamily>();
        public List<string> Warnings = new List<string>();
        public int FileCount;
    }
    public static class Catalog {
        static readonly Regex StrengthPattern = new Regex(@"\s+(25|50|75|100)%$", RegexOptions.CultureInvariant);
        public static string Normalize(string text) { return Regex.Replace((text ?? "").Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"[\s_\-]+", ""); }
        // Stable family IDs preserve existing favorites; this is not a file integrity check.
        public static string HashText(string text) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", ""); }
        public static XDocument ReadXml(string path) {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings)) return XDocument.Load(reader);
        }
        public static IEnumerable<string> FindRoots(string appRoot) {
            string installed = System.IO.Path.Combine(appRoot, "Styles");
            if (Directory.Exists(installed)) foreach (var d in Directory.GetDirectories(installed))
                if (System.IO.Path.GetFileName(d).IndexOf("RNI", StringComparison.OrdinalIgnoreCase) >= 0) yield return d;
            foreach (var sub in new [] { "Styles", "Styles50" }) {
                string users = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaptureOne", sub);
                if (Directory.Exists(users)) foreach (var d in Directory.GetDirectories(users))
                    if (System.IO.Path.GetFileName(d).IndexOf("RNI", StringComparison.OrdinalIgnoreCase) >= 0) yield return d;
            }
        }
        public static StyleCatalog Read(IEnumerable<string> roots, string appRoot) {
            var result = new StyleCatalog();
            var groups = new Dictionary<string, FilmFamily>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) {
                foreach (var file in Directory.GetFiles(root, "*.costyle", SearchOption.AllDirectories).OrderBy(s => s, StringComparer.OrdinalIgnoreCase)) {
                    try {
                        var entries = ReadXml(file).Descendants("E").Where(e => e.Attribute("K") != null).ToDictionary(e => (string)e.Attribute("K"), e => (string)e.Attribute("V") ?? "");
                        string name, uuid, icc;
                        if (!entries.TryGetValue("Name", out name) || !entries.TryGetValue("UUID", out uuid) || !entries.TryGetValue("ICCProfile", out icc)) continue;
                        Guid parsed;
                        if (!Guid.TryParse(uuid, out parsed)) { result.Warnings.Add("样式 UUID 无效：" + file); continue; }
                        var match = StrengthPattern.Match(name);
                        if (!match.Success) { result.Warnings.Add("未识别四档强度，已略过：" + name); continue; }
                        int strength = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        string baseName = name.Substring(0, match.Index).Trim();
                        string relative = System.IO.Path.GetDirectoryName(file.Substring(root.Length).TrimStart('\\', '/')) ?? "";
                        string familyFolder = Regex.Replace(relative, @"(^|[\\/])(25|50|75|100)%([\\/]|$)", "$1", RegexOptions.CultureInvariant).Trim('\\', '/');
                        string folder = familyFolder.Replace("RNI FILMS 5", "").Replace("RNI Films 5", "").Trim(' ', '\\', '/');
                        string key = Normalize(root + "|" + familyFolder + "|" + baseName);
                        FilmFamily family;
                        if (!groups.TryGetValue(key, out family)) {
                            string classification = (folder + " " + root + " " + baseName).ToLowerInvariant();
                            family = new FilmFamily { Id = HashText(key).Substring(0, 24), Name = baseName, Folder = folder,
                                Category = folder.Split('\\', '/')[0], Grain = classification.Contains("颗粒") || classification.Contains("grain"),
                                Rendered = classification.Contains("jpeg") || classification.Contains("jpg") || classification.Contains("tiff") };
                            groups.Add(key, family);
                        }
                        if (family.At(strength) != null) { result.Warnings.Add("重复强度未自动覆盖：" + file); continue; }
                        bool profileExists = File.Exists(System.IO.Path.Combine(appRoot, "Color Profiles", "Common", icc)) ||
                            File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaptureOne", "Color Profiles", icc));
                        var nativePath=new List<string> { System.IO.Path.GetFileName(root.TrimEnd('\\','/')) };
                        nativePath.AddRange((System.IO.Path.GetDirectoryName(file.Substring(root.Length).TrimStart('\\','/'))??"")
                            .Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries));
                        nativePath.Add(name);
                        family.Styles.Add(new FilmStyle { Path = file, Name = name, Uuid = parsed.ToString("B").ToUpperInvariant(), Icc = icc,
                            Strength = strength, IccExists = profileExists, NativePath=nativePath.ToArray() });
                        result.FileCount++;
                    } catch (Exception e) { result.Warnings.Add(System.IO.Path.GetFileName(file) + "：" + e.Message); }
                }
            }
            result.Families = groups.Values.OrderBy(f => f.Grain).ThenBy(f => f.Rendered).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Folder).ToList();
            return result;
        }
    }
    public sealed class PanelPreferences {
        public int Version = 1;
        public List<string> Favorites = new List<string>();
        public bool FavoritesInitialized;
        public bool FavoritesOnly = true;
        public bool TopMost = true;
        public bool WindowPlacementSaved;
        public int WindowLeft,WindowTop,WindowWidth,WindowHeight;
    }
    public sealed class PreferencesStore {
        readonly string directory;
        bool loadFailed;
        public string DirectoryPath { get { return directory; } }
        public PreferencesStore(string path) { directory = path; }
        public static string SharedDirectory {get{return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RniPalette");}}
        public bool ImportIfMissing(string seedFile) {
            string destination=System.IO.Path.Combine(directory,"favorites.json");
            if(File.Exists(destination)||!File.Exists(seedFile))return false;
            Directory.CreateDirectory(directory);File.Copy(seedFile,destination,false);return true;
        }
        public PanelPreferences Load() {
            try {return LoadChecked();} catch {loadFailed=true;throw;}
        }
        PanelPreferences LoadChecked() {
            string file = System.IO.Path.Combine(directory, "favorites.json");
            if (!File.Exists(file)) return new PanelPreferences();
            if (new FileInfo(file).Length > 1048576) throw new InvalidDataException("收藏文件过大，拒绝加载。");
            var data = new JavaScriptSerializer().Deserialize<PanelPreferences>(File.ReadAllText(file, Encoding.UTF8));
            if (data == null || data.Version != 1 || data.Favorites == null) throw new InvalidDataException("收藏文件版本不兼容。");
            return data;
        }
        public void Save(PanelPreferences value) {
            if(loadFailed) throw new InvalidOperationException("收藏加载失败后已禁用保存，原文件保持不动。");
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "favorites.json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
    }
    public sealed class StyleBinding {
        public string FamilyId;
        public FilmStyle Style;
        public int Shortcut;
        public string Display;
        public string[] NativePath {get{return Style==null?null:Style.NativePath;}}
        public bool UsesNativeTree {get{return Shortcut==0;}}
    }
    public static class Shortcuts {
        public const string SetName = "RNI Panel Demo";
        public static string DirectoryPath { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaptureOne", "CustomCommands"); } }
        public static string InstalledPath { get { return System.IO.Path.Combine(DirectoryPath, SetName + ".xml"); } }
        public static List<StyleBinding> CreateBindings(StyleCatalog catalog) {
            return CreateBindings(catalog,InstalledPath);
        }
        public static List<StyleBinding> CreateBindings(StyleCatalog catalog,string shortcutPath) {
            var result = new List<StyleBinding>();
            XDocument shortcuts=null;
            if(!String.IsNullOrWhiteSpace(shortcutPath)&&File.Exists(shortcutPath))shortcuts=Catalog.ReadXml(shortcutPath);
            foreach (var family in catalog.Families) {
                foreach(var style in family.Styles.Where(s=>s.IccExists).OrderBy(s=>s.Strength)) {
                    int shortcut=0;
                    if(shortcuts!=null) {
                        var matches=shortcuts.Descendants("AdvancedCommand").Where(e=>(string)e.Attribute("AdvancedCommandGroup")=="ApplyStyle"&&
                            SameUuid((string)e.Attribute("AdvancedCommandItem"),style.Uuid)).ToArray();
                        int key;
                        if(matches.Length==1&&Int32.TryParse((string)matches[0].Attribute("CommandShortcut"),out key)&&key!=0&&
                            shortcuts.Descendants().Count(e=>(int?)e.Attribute("CommandShortcut")==key)==1)shortcut=key;
                    }
                    result.Add(new StyleBinding { FamilyId=family.Id,Style=style,Shortcut=shortcut,
                        Display=shortcut==0?"C1 原生样式树":"已安装原生快捷键" });
                }
            }
            return result;
        }
        static bool SameUuid(string first,string second) {
            Guid a,b;return Guid.TryParse(first,out a)&&Guid.TryParse(second,out b)&&a==b;
        }
        public static XDocument BuildSet(string source, IEnumerable<StyleBinding> bindings) {
            var xml = Catalog.ReadXml(source);
            if (xml.Root == null || xml.Root.Name != "Commands") throw new InvalidDataException("不是有效 C1 键集。");
            // C1 16.7.8 keeps these built-in default bindings in managed code, not
            // in the default XML. Materialize them when cloning that default set.
            if (System.IO.Path.GetFileNameWithoutExtension(source) == "CaptureOne Default") PreserveDefaultAdvanced(xml);
            var used = new HashSet<int>(xml.Descendants().Attributes("CommandShortcut").Select(a => int.Parse(a.Value, CultureInfo.InvariantCulture)).Where(k => k != 0));
            var array = bindings.Where(b=>b.Shortcut!=0).ToArray();
            if (array.Select(b => b.Shortcut).Distinct().Count() != array.Length) throw new InvalidDataException("样式键位重复。");
            if (array.Any(b => used.Contains(b.Shortcut))) throw new InvalidDataException("样式键位与来源键集冲突，未生成。");
            var advanced = xml.Root.Element("AdvancedCommands");
            if (advanced == null) { advanced = new XElement("AdvancedCommands"); xml.Root.Add(advanced); }
            foreach (var binding in array) advanced.Add(new XElement("AdvancedCommand", new XAttribute("AdvancedCommandGroup", "ApplyStyle"),
                new XAttribute("AdvancedCommandItem", binding.Style.Uuid), new XAttribute("AdvancedCommandItemText", binding.Style.Name), new XAttribute("CommandShortcut", binding.Shortcut)));
            return xml;
        }
        static void PreserveDefaultAdvanced(XDocument xml) {
            var container = xml.Root.Element("AdvancedCommands");
            if (container == null) { container = new XElement("AdvancedCommands"); xml.Root.Add(container); }
            string[][] defaults = {
                new [] {"ApplyStyle","feda3238-0c9b-4e99-86ce-e64858c4a06a","Bright Contrast","262193"},
                new [] {"ApplyStyle","68279303-cf1e-4b6f-b651-db4b576e366d","Pastel Spring","262194"},
                new [] {"ApplyStyle","b4eb3f4f-d460-459e-bc94-ecef93a885ce","B&W Soft","262195"},
                new [] {"ApplyStyle","0a30ee56-2e4f-47ec-bfb5-d2a2c9123213","Film F400","262196"},
                new [] {"ApplyStyle","aaeac642-ddfb-496e-8ea8-b4eac12a9da0","Film K100","262197"},
                new [] {"SelectStyleBrush","0c22d681-89b4-41a3-a863-f5ee32535cf5","Dodge (brighten)","393265"},
                new [] {"SelectStyleBrush","9c4c5ddd-0512-4398-8c82-cc23aeeea8bf","Burn (darken)","393266"},
                new [] {"SelectStyleBrush","48a63db1-b9ba-4070-913d-4cd32236e66f","Add Detail","393267"},
                new [] {"SelectStyleBrush","e7934952-67cf-47cd-9347-81af8e2e1810","Shadows (recover)","393268"},
                new [] {"SelectStyleBrush","08578635-fde8-4dab-953b-620fae64fa23","Highlights (recover)","393269"}
            };
            foreach (var row in defaults) {
                bool exists = container.Elements("AdvancedCommand").Any(e => (string)e.Attribute("AdvancedCommandGroup") == row[0] &&
                    ((string)e.Attribute("AdvancedCommandItem") ?? "").Trim('{','}').Equals(row[1],StringComparison.OrdinalIgnoreCase));
                if (!exists) container.Add(new XElement("AdvancedCommand",new XAttribute("AdvancedCommandGroup",row[0]),new XAttribute("AdvancedCommandItem",row[1]),
                    new XAttribute("AdvancedCommandItemText",row[2]),new XAttribute("CommandShortcut",row[3])));
            }
        }
        public static void InstallNew(string source, string destination, IEnumerable<StyleBinding> bindings) {
            if (File.Exists(destination)) throw new IOException("已有同名个人键集；不会覆盖，请先核对。");
            var xml = BuildSet(source, bindings);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) xml.Save(output);
        }
        public static bool Verify(string path, IEnumerable<StyleBinding> bindings, out string reason) {
            try {
                var all=bindings.ToArray();
                var shortcutBindings=all.Where(b=>b.Shortcut!=0).ToArray();
                var xml = shortcutBindings.Length==0?null:Catalog.ReadXml(path);
                foreach (var binding in bindings) {
                    if (!File.Exists(binding.Style.Path)) throw new InvalidDataException("样式文件已移除，请重开面板重新扫描。");
                    if(binding.Shortcut==0) {
                        if(binding.NativePath==null||binding.NativePath.Length<2)throw new InvalidDataException("原生样式路径不完整："+binding.Style.Name);
                        continue;
                    }
                    var nodes = xml.Descendants("AdvancedCommand").Where(e => (string)e.Attribute("AdvancedCommandGroup") == "ApplyStyle" &&
                        SameUuid((string)e.Attribute("AdvancedCommandItem"),binding.Style.Uuid)).ToArray();
                    if (nodes.Length != 1 || (int?)nodes[0].Attribute("CommandShortcut") != binding.Shortcut) throw new InvalidDataException("样式绑定不一致：" + binding.Style.Name);
                    if (xml.Descendants().Where(e => (int?)e.Attribute("CommandShortcut") == binding.Shortcut).Count() != 1) throw new InvalidDataException("快捷键存在重复目标。");
                }
                reason = all.Length+" 个样式入口（"+shortcutBindings.Length+" 个快捷键，其余走原生样式树；运行时仍须确认控件）"; return true;
            } catch (Exception e) { reason = e.Message; return false; }
        }
    }
    public sealed class TargetSnapshot {
        public IntPtr Handle;
        public int ProcessId;
        public long ProcessStartTicks;
        public string DocumentPath;
        public string Title;
        public string VariantName;
        public string FileName;
        public int VariantId;
        public string VariantUuid;
        public int SelectedCount;
        public bool Modal;
        public bool Enabled;
        public string Diagnostics;
    }
    public static class SafetyPolicy {
        public const string DemoCatalogPath=@"D:\照片\work\rni-panel\qa\RNI-Panel-Sandbox\RNI-Panel-Sandbox.cocatalogdb";
        public const string MainCatalogPath=@"D:\照片\Catalogs\Photography-Master\Photography-Master.cocatalogdb";
        public static bool IsAllowedCatalog(string path) {
            if(String.IsNullOrWhiteSpace(path))return false;
            string full;
            try{full=System.IO.Path.GetFullPath(path);}catch{return false;}
            return String.Equals(full,DemoCatalogPath,StringComparison.OrdinalIgnoreCase)||String.Equals(full,MainCatalogPath,StringComparison.OrdinalIgnoreCase);
        }
        public static string CheckDocument(TargetSnapshot target,TargetSnapshot connected) {
            if(connected==null)return "尚未连接图库。";
            if(target==null||target.Handle==IntPtr.Zero)return "没有唯一可用的 Capture One 窗口。";
            if(!target.Enabled||target.Modal)return "C1 有弹窗或窗口不可操作。";
            if(target.Handle!=connected.Handle||target.ProcessId!=connected.ProcessId||target.ProcessStartTicks!=connected.ProcessStartTicks)
                return "C1 窗口或进程已更换，请重新连接图库。";
            if(!String.Equals(target.Title,connected.Title,StringComparison.Ordinal)||String.IsNullOrWhiteSpace(target.DocumentPath)||
                !String.Equals(target.DocumentPath,connected.DocumentPath,StringComparison.OrdinalIgnoreCase))
                return "C1 图库已切换，请重新连接；未发送。";
            return null;
        }
        public static string Check(TargetSnapshot target,TargetSnapshot armed) {
            if(armed==null) return "尚未确定本次照片。";
            string basic=Check(target,armed.VariantId,armed.Title);
            if(basic!=null)return basic;
            return CheckPrimary(target,armed);
        }
        // This identity check does not authorize multi-edit. The batch adapter
        // must independently read C1's Edit All Selected Variants state as OFF
        // immediately before each command. Do not substitute a fake Count=1.
        public static string CheckPrimary(TargetSnapshot target,TargetSnapshot armed) {
            if(armed==null)return "尚未确定本次主图。";
            string basic=CheckDocument(target,armed);
            if(basic!=null)return basic;
            if(target.SelectedCount<1||target.SelectedCount!=armed.SelectedCount)return "本次选区数量已改变，未发送。";
            if(target.VariantId<=0||target.VariantId!=armed.VariantId)return "执行期间主图已改变，本次未发送。";
            if(target.Handle!=armed.Handle||target.ProcessId!=armed.ProcessId||target.ProcessStartTicks!=armed.ProcessStartTicks)
                return "C1 窗口或进程已更换，请重新连接。";
            if(String.IsNullOrWhiteSpace(target.DocumentPath)||!String.Equals(target.DocumentPath,armed.DocumentPath,StringComparison.OrdinalIgnoreCase))
                return "实际文档路径不能确认或已经切换，未发送。";
            if(String.IsNullOrWhiteSpace(target.VariantUuid)||!String.Equals(target.VariantUuid,armed.VariantUuid,StringComparison.OrdinalIgnoreCase))return "变体唯一标识不能确认或已改变，未发送。";
            return null;
        }
        public static string Check(TargetSnapshot target, int armedId, string armedTitle) {
            if (target == null || target.Handle == IntPtr.Zero) return "没有唯一可用的 Capture One 窗口。";
            if (!target.Enabled || target.Modal) return "C1 有弹窗或窗口不可操作，已阻止。";
            if (!String.Equals(target.Title, armedTitle, StringComparison.Ordinal)) return "C1 文档已切换，请重新连接。";
            if (target.SelectedCount != 1) return "请只选中一张照片；当前不是单选。";
            if (armedId <= 0 || target.VariantId != armedId) return "执行期间照片已改变，本次未发送。";
            return null;
        }
    }
    public static class SelectionSummary {
        public static bool IsSingle(string text) {
            int selected;
            return TryCount(text,out selected)&&selected==1;
        }
        public static bool TryCount(string text,out int selected) {
            selected=0;
            var match=Regex.Match(text??"",@"^\s*(\d[\d,\u00a0 ]*)\s*/\s*(\d[\d,\u00a0 ]*)\s*(?:\([^)]*\)|（[^）]*）)?\s*$");
            if(!match.Success)return false;
            int total;
            Func<string,string> digits=value=>Regex.Replace(value,@"[,\s]","");
            return Int32.TryParse(digits(match.Groups[1].Value),out selected)&&Int32.TryParse(digits(match.Groups[2].Value),out total)&&selected>=0&&total>=selected&&total>=1;
        }
    }
    public sealed class AttemptGate {
        long version;
        public long Capture(){return Interlocked.Read(ref version);}
        public void Cancel(){Interlocked.Increment(ref version);}
        public bool IsCurrent(long token){return token==Interlocked.Read(ref version);}
    }
    public static class NativeStylePolicy {
        // True means a new native command is necessary. Unlike a remembered
        // button click or an eventually-saved database row, this decision must
        // receive the currently visible native applied-style list.
        public static bool NeedsSend(IEnumerable<string> activeStyles,string requested,IEnumerable<string> supported) {
            if(activeStyles==null)throw new InvalidOperationException("无法读取 C1 当前样式，未发送。");
            var active=activeStyles.ToArray();
            var known=new HashSet<string>(supported??Enumerable.Empty<string>(),StringComparer.Ordinal);
            if(String.IsNullOrWhiteSpace(requested)||!known.Contains(requested))throw new InvalidOperationException("请求不在已索引的 RNI 样式内，未发送。");
            if(active.Length>1)throw new InvalidOperationException("C1 存在多个已应用样式或预设，暂不自动替换混合效果，未发送。");
            if(active.Length==1&&!known.Contains(active[0]))throw new InvalidOperationException("当前样式不是已识别的 RNI 样式，未发送。");
            return active.Length==0||!String.Equals(active[0],requested,StringComparison.Ordinal);
        }
    }
    public interface ILiveStyleSession {
        bool IsCurrent { get; }
        Task ValidateTarget();
        Task<string[]> ReadAppliedStyles();
        Task SendShortcut();
    }
    public sealed class StyleApplyResult {
        public bool Sent;
        public string ConfirmedStyle;
    }
    public interface IClearStyleSession {
        bool IsCurrent {get;}
        Task ValidateTarget();
        Task<string[]> ReadAppliedStyles();
        Task RemoveCurrentStyle(string styleName);
    }
    public sealed class StyleClearResult {
        public bool Sent;
        public string RemovedStyle;
        public string[] RemovedStyles;
    }
    public static class StyleClearWorkflow {
        static void RequireCurrent(IClearStyleSession session) {
            if(!session.IsCurrent)throw new OperationCanceledException("连接已取消；不再执行后续动作。");
        }
        public static async Task<StyleClearResult> Run(IClearStyleSession session,IEnumerable<string> supported) {
            RequireCurrent(session);
            await session.ValidateTarget();
            RequireCurrent(session);
            var before=await session.ReadAppliedStyles();
            RequireCurrent(session);
            if(before==null)throw new InvalidOperationException("未读到当前样式，未清除。");
            var known=new HashSet<string>(supported??Enumerable.Empty<string>(),StringComparer.Ordinal);
            var remove=before.Where(known.Contains).ToArray();
            if(remove.Distinct(StringComparer.Ordinal).Count()!=remove.Length)
                throw new InvalidOperationException("当前 RNI 样式身份重复，无法安全逐项清除；未发送。");
            await session.ValidateTarget();
            RequireCurrent(session);
            if(remove.Length==0)return new StyleClearResult {Sent=false,RemovedStyles=new string[0]};
            var remaining=before.ToList();
            foreach(string identity in remove) {
                await session.RemoveCurrentStyle(identity); // Remove this RNI only, never reset all adjustments.
                RequireCurrent(session);
                remaining.Remove(identity);
                var after=await session.ReadAppliedStyles();
                RequireCurrent(session);
                await session.ValidateTarget();
                RequireCurrent(session);
                if(after==null||!remaining.SequenceEqual(after,StringComparer.Ordinal))
                    throw new InvalidOperationException("已发送清除，但未确认只移除了目标 RNI；已停止，不会自动重发。其他调整未被主动重置。");
            }
            return new StyleClearResult {Sent=true,RemovedStyle=String.Join("、",remove),RemovedStyles=remove};
        }
    }
    // Batch never sends a toggle to a heterogeneous selection. The adapter must
    // make C1 edit only its primary variant; each item then uses the same native
    // read/decision/confirmation workflow as a single photo.
    public interface IBatchStyleSession {
        bool IsCurrent {get;}
        Task<TargetSnapshot> InspectPrimary();
        Task BeginPrimaryOnly();
        Task SelectFirst();
        Task SelectNext();
        Task<bool> ExecutePrimary(TargetSnapshot expected);
        Task RestoreEditMode();
    }
    public sealed class BatchStyleResult {
        public int Total;
        public int Confirmed;
        public int Sent;
        public bool PrimaryRestored;
        public bool EditModeRestored;
    }
    public sealed class BatchStyleException:InvalidOperationException {
        public BatchStyleResult Result {get;private set;}
        public BatchStyleException(BatchStyleResult result,Exception inner):base(
            "批量已停止：已确认 "+result.Confirmed+"/"+result.Total+" 张（发送 "+result.Sent+" 张）。"+
            "未确认的当前照片不会自动重试。"+(result.EditModeRestored?"":"编辑模式可能仍为仅主图；请查看 C1。")+" "+inner.Message,inner) {Result=result;}
    }
    public static class BatchStyleWorkflow {
        static void RequireCurrent(IBatchStyleSession session) {
            if(!session.IsCurrent)throw new OperationCanceledException("批量已取消，不继续导航或发送。");
        }
        static void CheckPrimary(TargetSnapshot current,TargetSnapshot original,TargetSnapshot planned) {
            string reason=SafetyPolicy.CheckDocument(current,original);
            if(reason!=null)throw new InvalidOperationException(reason);
            if(current.SelectedCount!=original.SelectedCount)throw new InvalidOperationException("批量选区数量已改变，已停止。");
            if(current.VariantId<=0||String.IsNullOrWhiteSpace(current.VariantUuid))
                throw new InvalidOperationException("不能唯一识别当前主图，已停止。");
            if(planned!=null&&(current.VariantId!=planned.VariantId||!String.Equals(current.VariantUuid,planned.VariantUuid,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("当前主图与本次锁定选区不一致，已停止。");
        }
        static async Task<TargetSnapshot> Read(IBatchStyleSession session,TargetSnapshot original,TargetSnapshot planned) {
            RequireCurrent(session);
            var current=await session.InspectPrimary();
            RequireCurrent(session);
            CheckPrimary(current,original,planned);
            return current;
        }
        public static async Task<BatchStyleResult> Run(IBatchStyleSession session,Action<int,int,TargetSnapshot,bool> confirmed=null) {
            var result=new BatchStyleResult();
            try {
                RequireCurrent(session);
                var original=await session.InspectPrimary();
                RequireCurrent(session);
                if(original==null||original.SelectedCount<2)throw new InvalidOperationException("批量需要至少两张已选照片。");
                if(!SafetyPolicy.IsAllowedCatalog(original.DocumentPath))throw new InvalidOperationException("当前不是支持的图库，未开始批量。");
                CheckPrimary(original,original,original);
                result.Total=original.SelectedCount;
                await session.BeginPrimaryOnly();
                RequireCurrent(session);
                await session.SelectFirst();
                var plan=new List<TargetSnapshot>();
                for(int i=0;i<result.Total;i++) {
                    var item=await Read(session,original,null);
                    if(plan.Any(p=>String.Equals(p.VariantUuid,item.VariantUuid,StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("原生主图导航没有覆盖完整选区，未开始批量应用。");
                    plan.Add(item);
                    if(i+1<result.Total) {RequireCurrent(session);await session.SelectNext();}
                }
                int originalIndex=plan.FindIndex(p=>p.VariantId==original.VariantId&&String.Equals(p.VariantUuid,original.VariantUuid,StringComparison.OrdinalIgnoreCase));
                if(originalIndex<0)throw new InvalidOperationException("选区中缺少原主图，未开始批量应用。");
                RequireCurrent(session);
                await session.SelectFirst();
                for(int i=0;i<plan.Count;i++) {
                    var current=await Read(session,original,plan[i]);
                    bool sent=await session.ExecutePrimary(current);
                    RequireCurrent(session);
                    await Read(session,original,plan[i]);
                    result.Confirmed++;
                    if(sent)result.Sent++;
                    if(confirmed!=null)confirmed(result.Confirmed,result.Total,current,sent);
                    if(i+1<plan.Count) {RequireCurrent(session);await session.SelectNext();}
                }
                // Restore only through the already validated sequence. On any
                // cancellation or unknown native outcome leave the current photo
                // in place so the user can inspect it; never move blindly.
                RequireCurrent(session);
                await session.SelectFirst();
                for(int i=0;i<=originalIndex;i++) {
                    await Read(session,original,plan[i]);
                    if(i<originalIndex) {RequireCurrent(session);await session.SelectNext();}
                }
                result.PrimaryRestored=true;
                RequireCurrent(session);
                await session.RestoreEditMode();
                result.EditModeRestored=true;
                return result;
            } catch(Exception e) {throw new BatchStyleException(result,e);}
        }
    }
    public static class StyleWorkflow {
        static void RequireCurrent(ILiveStyleSession session) {
            if(!session.IsCurrent)throw new OperationCanceledException("连接已取消；不再执行后续动作。");
        }
        public static async Task<StyleApplyResult> Run(ILiveStyleSession session,string requested,IEnumerable<string> supported) {
            RequireCurrent(session);
            await session.ValidateTarget();
            RequireCurrent(session);
            var before=await session.ReadAppliedStyles();
            RequireCurrent(session);
            bool needsSend=NativeStylePolicy.NeedsSend(before,requested,supported);
            await session.ValidateTarget();
            RequireCurrent(session);
            if(!needsSend)return new StyleApplyResult {Sent=false,ConfirmedStyle=requested};
            await session.SendShortcut(); // Never retry a toggle command, even if confirmation fails.
            RequireCurrent(session);
            var after=await session.ReadAppliedStyles();
            RequireCurrent(session);
            await session.ValidateTarget();
            RequireCurrent(session);
            if(after==null||after.Length!=1||!String.Equals(after[0],requested,StringComparison.Ordinal))
                throw new InvalidOperationException("已发送，但 C1 未确认预期样式；已停止，不会自动重发。请查看 C1 当前样式。");
            return new StyleApplyResult {Sent=true,ConfirmedStyle=requested};
        }
    }
}
