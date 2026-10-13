using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SaveHarbor {
 [DataContract] public class Rule {
  [DataMember] public string Id = Guid.NewGuid().ToString("N");
  [DataMember] public string Anchor = "LocalAppData";
  [DataMember] public string Relative = "";
  [DataMember] public string Root = "";
  [DataMember] public bool IsFile;
  public Rule Portable() { return new Rule { Id=Id, Anchor=Anchor, Relative=Relative, IsFile=IsFile }; }
 }
 [DataContract] public class Game {
  [DataMember] public string Id=Guid.NewGuid().ToString("N");
  [DataMember] public string Name="";
  [DataMember] public string Exe="";
  [DataMember] public List<Rule> Rules=new List<Rule>();
  [DataMember] public string LastSynced="";
  [DataMember] public string Head="";
  [DataMember] public bool AutoBackup;
  public override string ToString() { return Name; }
  public Game Portable() { return new Game { Id=Id, Name=Name, Rules=Rules.Select(x=>x.Portable()).ToList() }; }
 }
 [DataContract] public class Settings {
  [DataMember] public List<Game> Games=new List<Game>();
  [DataMember] public string Library="";
 }
 [DataContract] public class SaveFile {
  [DataMember] public string Entry;
  [DataMember] public long Size;
  [DataMember] public string Hash;
 }
 [DataContract] public class Snapshot {
  [DataMember] public string Id=Guid.NewGuid().ToString("N");
  [DataMember] public string GameId;
  [DataMember] public string Parent="";
  [DataMember] public string Created=DateTime.UtcNow.ToString("o");
  [DataMember] public string Device=Environment.MachineName;
  [DataMember] public string Fingerprint;
  [DataMember] public string ZipHash;
  [DataMember] public string Note="手动备份";
  [DataMember] public List<SaveFile> Files=new List<SaveFile>();
  [DataMember] public List<string> RuleIds=new List<string>();
  public override string ToString() { return DateTime.Parse(Created).ToLocalTime().ToString("MM-dd HH:mm:ss")+" · "+Device+" · "+Note; }
 }
 public static class Json {
  public static byte[] Encode<T>(T value) { using(var s=new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(s,value); return s.ToArray(); } }
  public static T Read<T>(string p) { using(var s=File.OpenRead(p)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s); }
  public static void Write<T>(string p,T value) {
   Directory.CreateDirectory(Path.GetDirectoryName(p)); var tmp=p+"."+Guid.NewGuid().ToString("N")+".tmp";
   File.WriteAllBytes(tmp,Encode(value));
   if(File.Exists(p)) File.Replace(tmp,p,null); else File.Move(tmp,p);
  }
 }
 public static class Paths {
  public static string Base(string anchor) {
   switch(anchor) {
    case "Documents": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    case "LocalAppData": return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    case "AppData": return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    case "UserProfile": return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    default: return "";
   }
  }
  public static string Within(string root,string relative) {
   if(string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先绑定本机根目录。");
   if(Path.IsPathRooted(relative)) throw new InvalidDataException("相对路径不能包含盘符或绝对路径。");
   var r=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
   var p=Path.GetFullPath(Path.Combine(r,relative));
   if(!p.Equals(r,StringComparison.OrdinalIgnoreCase) && !p.StartsWith(r+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径不能越出所选根目录。");
   return p;
  }
  public static string Resolve(Rule r) { var b=Base(r.Anchor); return Within(b.Length>0?b:r.Root,r.Relative); }
  public static Rule From(string value) {
   var p=Path.GetFullPath(value.Trim().Trim('"'));
   if(!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException("所选存档路径不存在。",p);
   foreach(var a in new[]{"Documents","LocalAppData","AppData","UserProfile"}) {
    var b=Base(a).TrimEnd('\\');
    if(b.Length>0 && p.StartsWith(b+"\\",StringComparison.OrdinalIgnoreCase)) return new Rule { Anchor=a, Relative=p.Substring(b.Length+1), IsFile=File.Exists(p) };
   }
   return new Rule { Anchor="CustomRoot", Root=Path.GetDirectoryName(p), Relative=Path.GetFileName(p), IsFile=File.Exists(p) };
  }
  public static void NoLinks(string p) {
   for(var d=Path.GetFullPath(p); !string.IsNullOrEmpty(d); d=Path.GetDirectoryName(d)) {
    if((File.Exists(d)||Directory.Exists(d)) && (File.GetAttributes(d)&FileAttributes.ReparsePoint)!=0) throw new IOException("暂不支持链接或重解析点："+d);
   }
  }
  public static IEnumerable<string> Files(string dir) {
   NoLinks(dir);
   foreach(var f in Directory.GetFiles(dir)) { NoLinks(f); yield return f; }
   foreach(var d in Directory.GetDirectories(dir)) foreach(var f in Files(d)) yield return f;
  }
 }
 // Cloud implementations can stream archives through this same contract. Device
 // paths and game executables are never included in the portable catalog.
 public interface IStorageProvider {
  void Publish(Game game,Snapshot snapshot,string localFolder);
  IEnumerable<Game> Games();
  IEnumerable<Snapshot> Versions(Game game);
  void Download(Snapshot snapshot,string localFolder);
 }
 public class FolderStorage : IStorageProvider {
  readonly string root;
  public FolderStorage(string rootPath) {
   if(string.IsNullOrWhiteSpace(rootPath)||!Directory.Exists(rootPath)) throw new DirectoryNotFoundException("请先设置一个已存在的文件夹存档库。");
   root=Path.GetFullPath(rootPath); Paths.NoLinks(root);
  }
  static string Id(string id) { Guid value; if(!Guid.TryParseExact(id,"N",out value)) throw new InvalidDataException("存档库标识无效。"); return id; }
  string VersionFolder(Snapshot s) { return Path.Combine(root,Id(s.GameId),Id(s.Id)); }
  public void Publish(Game g,Snapshot s,string localFolder) {
   if(s.GameId!=g.Id) throw new InvalidDataException("版本与游戏不匹配。");
   var dir=Path.Combine(root,Id(g.Id)); Directory.CreateDirectory(dir); Paths.NoLinks(dir);
   var dest=VersionFolder(s);
   if(Directory.Exists(dest)) {
    Paths.NoLinks(dest);
    if(!File.Exists(Path.Combine(dest,"manifest.json"))||Engine.HashFile(Path.Combine(dest,"save.zip"))!=s.ZipHash) throw new IOException("同名版本不完整或内容不同。");
   } else {
    var temp=Path.Combine(dir,"upload-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
    try {
     File.Copy(Path.Combine(localFolder,"save.zip"),Path.Combine(temp,"save.zip"));
     if(Engine.HashFile(Path.Combine(temp,"save.zip"))!=s.ZipHash) throw new InvalidDataException("传输校验失败。");
     Json.Write(Path.Combine(temp,"manifest.json"),s); Directory.Move(temp,dest);
    } finally { if(Directory.Exists(temp)) Directory.Delete(temp,true); }
   }
   Json.Write(Path.Combine(dir,"game.json"),g.Portable());
  }
  public IEnumerable<Game> Games() {
   foreach(var dir in Directory.GetDirectories(root)) {
    if(!File.Exists(Path.Combine(dir,"game.json"))) continue; Paths.NoLinks(dir);
    var game=Json.Read<Game>(Path.Combine(dir,"game.json"));
    if(Id(game.Id)!=Path.GetFileName(dir)) throw new InvalidDataException("存档库游戏标识与目录不同。");
    yield return game.Portable();
   }
  }
  public IEnumerable<Snapshot> Versions(Game g) {
   foreach(var dir in Directory.GetDirectories(Path.Combine(root,Id(g.Id)))) {
    if(!File.Exists(Path.Combine(dir,"manifest.json"))) continue; Paths.NoLinks(dir);
    var s=Json.Read<Snapshot>(Path.Combine(dir,"manifest.json"));
    if(Id(s.Id)!=Path.GetFileName(dir)||s.GameId!=g.Id) throw new InvalidDataException("存档库版本标识不一致。");
    yield return s;
   }
  }
  public void Download(Snapshot s,string localFolder) {
   var source=VersionFolder(s); Paths.NoLinks(source); var zip=Path.Combine(source,"save.zip"); Paths.NoLinks(zip);
   File.Copy(zip,Path.Combine(localFolder,"save.zip"));
  }
 }
 public class Engine {
  public string Data;
  public Settings State;
  public Engine(string data) { Data=Path.GetFullPath(data); Directory.CreateDirectory(Data); State=File.Exists(Path.Combine(Data,"settings.json"))?Json.Read<Settings>(Path.Combine(Data,"settings.json")):new Settings(); }
  public void Persist() { Json.Write(Path.Combine(Data,"settings.json"),State); }
  public static string Hash(byte[] bytes) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
  public static string HashFile(string p) { using(var sha=SHA256.Create()) using(var f=File.OpenRead(p)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
  public static string Fingerprint(IEnumerable<SaveFile> files) { return Hash(Encoding.UTF8.GetBytes(string.Join("\n",files.OrderBy(x=>x.Entry,StringComparer.Ordinal).Select(x=>x.Entry+"|"+x.Size+"|"+x.Hash)))); }
  public List<KeyValuePair<string,string>> Source(Game game) {
   if(game.Rules.Count==0) throw new InvalidOperationException("请至少设置一个存档位置。");
   var result=new List<KeyValuePair<string,string>>();
   foreach(var r in game.Rules) {
    var p=Paths.Resolve(r); Paths.NoLinks(p);
    var data=Data.TrimEnd('\\')+"\\";
    if(p.Equals(Data,StringComparison.OrdinalIgnoreCase)||p.StartsWith(data,StringComparison.OrdinalIgnoreCase)||Data.StartsWith(p.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("存档路径不能与软件数据目录重叠。");
    if(!string.IsNullOrEmpty(State.Library)) {
     var lib=Path.GetFullPath(State.Library).TrimEnd('\\');
     if(p.Equals(lib,StringComparison.OrdinalIgnoreCase)||lib.StartsWith(p.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||p.StartsWith(lib+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("存档路径不能与同步库重叠。");
    }
    if(r.IsFile) {
     if(!File.Exists(p)) throw new FileNotFoundException("存档文件尚未找到："+p);
     result.Add(new KeyValuePair<string,string>(r.Id+"/file",p));
    } else {
     if(!Directory.Exists(p)) throw new DirectoryNotFoundException("存档目录尚未找到："+p);
     foreach(var f in Paths.Files(p)) result.Add(new KeyValuePair<string,string>(r.Id+"/"+f.Substring(p.TrimEnd('\\').Length+1).Replace('\\','/'),f));
    }
   }
   return result.OrderBy(x=>x.Key,StringComparer.Ordinal).ToList();
  }
  public List<SaveFile> Scan(Game game) { return Source(game).Select(x=>new SaveFile { Entry=x.Key,Size=new FileInfo(x.Value).Length,Hash=HashFile(x.Value) }).ToList(); }
  public string Folder(string gameId) { if(!Guid.TryParseExact(gameId,"N",out dummy)) throw new InvalidDataException("游戏标识无效。"); return Path.Combine(Data,"versions",gameId); }
  private Guid dummy;
  public List<Snapshot> Versions(Game g) {
   var p=Folder(g.Id); if(!Directory.Exists(p)) return new List<Snapshot>();
   return Directory.GetDirectories(p).Where(x=>File.Exists(Path.Combine(x,"manifest.json"))).Select(x=>Json.Read<Snapshot>(Path.Combine(x,"manifest.json"))).OrderByDescending(x=>x.Created,StringComparer.Ordinal).ToList();
  }
  public Snapshot Backup(Game game,string note,string parent) {
   var before=Scan(game); if(before.Count==0) throw new InvalidOperationException("存档为空，未创建备份。");
   var snap=new Snapshot { GameId=game.Id,Note=note,Parent=parent??"",Fingerprint=Fingerprint(before),Files=before,RuleIds=game.Rules.Select(r=>r.Id).ToList() };
   var temp=Path.Combine(Data,"staging",snap.Id); Directory.CreateDirectory(temp);
   try {
    var zipPath=Path.Combine(temp,"save.zip");
    using(var stream=File.Create(zipPath)) using(var zip=new ZipArchive(stream,ZipArchiveMode.Create)) {
     foreach(var pair in Source(game)) {
      var entry=zip.CreateEntry(pair.Key,CompressionLevel.Optimal);
      using(var src=File.OpenRead(pair.Value)) using(var dst=entry.Open()) src.CopyTo(dst);
     }
    }
    if(Fingerprint(Scan(game))!=snap.Fingerprint) throw new IOException("存档在备份过程中变化，请关闭游戏后重试。");
    Verify(zipPath,snap);
    snap.ZipHash=HashFile(zipPath); Json.Write(Path.Combine(temp,"manifest.json"),snap);
    var dest=Path.Combine(Folder(game.Id),snap.Id); Directory.CreateDirectory(Folder(game.Id)); Directory.Move(temp,dest); return snap;
   } finally { if(Directory.Exists(temp)) Directory.Delete(temp,true); }
  }
  public static void Verify(string zipPath,Snapshot s) {
   if(s.Files==null||s.Files.Count==0) throw new InvalidDataException("版本清单为空。");
   using(var zip=ZipFile.OpenRead(zipPath)) {
    if(zip.Entries.Count!=s.Files.Count) throw new InvalidDataException("备份条目数不一致。");
    var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var entry in zip.Entries) {
     if(!seen.Add(entry.FullName)) throw new InvalidDataException("备份存在重复路径。");
     var item=s.Files.SingleOrDefault(x=>x.Entry==entry.FullName);
     if(item==null||entry.Length!=item.Size) throw new InvalidDataException("备份条目与清单不一致。");
     using(var sha=SHA256.Create()) using(var src=entry.Open()) {
      var h=BitConverter.ToString(sha.ComputeHash(src)).Replace("-","").ToLowerInvariant();
      if(h!=item.Hash) throw new InvalidDataException("备份校验失败。");
     }
    }
   }
   if(Fingerprint(s.Files)!=s.Fingerprint) throw new InvalidDataException("版本内容指纹不一致。");
  }
  public void EnsureGameClosed(Game g) {
   if(string.IsNullOrWhiteSpace(g.Exe)) throw new InvalidOperationException("请先设置游戏 EXE，以便检查游戏是否运行。");
   if(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(g.Exe)).Length>0) throw new InvalidOperationException("检测到同名游戏进程，请退出游戏后操作。");
  }
  public void Restore(Game g,Snapshot s) {
   EnsureGameClosed(g); if(s.GameId!=g.Id) throw new InvalidDataException("版本属于其他游戏。");
   Guid versionId; if(!Guid.TryParseExact(s.Id,"N",out versionId)) throw new InvalidDataException("版本标识无效。");
   if(!new HashSet<string>(s.RuleIds).SetEquals(g.Rules.Select(r=>r.Id))) throw new InvalidDataException("这个版本的存档位置集合与当前档案不同。请先使配置位置与原备份一致。");
   var zipPath=Path.Combine(Folder(g.Id),s.Id,"save.zip");
   if(HashFile(zipPath)!=s.ZipHash) throw new InvalidDataException("备份包校验失败。"); Verify(zipPath,s);
   var targets=s.Files.Select(f=>Target(g,f.Entry)).ToList();
   if(targets.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=targets.Count) throw new InvalidDataException("多个存档条目映射到同一文件。");
   foreach(var r in g.Rules) Paths.NoLinks(Paths.Resolve(r));
   // A fresh device may have no local saves yet. Existing configured roots must all
   // be readable; partial local state is backed up before any restoration.
   var existing=new Game { Id=g.Id,Name=g.Name,Rules=g.Rules.Where(r=>File.Exists(Paths.Resolve(r))||Directory.Exists(Paths.Resolve(r))).ToList() };
   var oldHead=g.Head; Snapshot safety=null;
   if(existing.Rules.Count>0 && Source(existing).Count>0) safety=Backup(existing,"恢复前自动备份",g.Head);
   bool modifying=false;
   var stage=Path.Combine(Data,"restore",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
   try {
    using(var zip=ZipFile.OpenRead(zipPath)) foreach(var f in s.Files) {
     var staged=Paths.Within(stage,f.Entry.Replace('/','\\')); Directory.CreateDirectory(Path.GetDirectoryName(staged)); zip.GetEntry(f.Entry).ExtractToFile(staged);
    }
    modifying=true;
    foreach(var r in g.Rules) if(!r.IsFile) Directory.CreateDirectory(Paths.Resolve(r));
    foreach(var f in s.Files) {
     var target=Target(g,f.Entry); Paths.NoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target));
     var temp=target+".saveharbor-"+Guid.NewGuid().ToString("N");
     try { File.Copy(Paths.Within(stage,f.Entry.Replace('/','\\')),temp); if(File.Exists(target)) File.Replace(temp,target,null); else File.Move(temp,target); }
     finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    // Replace only files in configured roots. Extra files are removed after all
    // snapshot files are installed; their prior content is in the safety backup.
    var wanted=new HashSet<string>(s.Files.Select(f=>Target(g,f.Entry)),StringComparer.OrdinalIgnoreCase);
    foreach(var pair in Source(g)) if(!wanted.Contains(pair.Value)) File.Delete(pair.Value);
    g.Head=s.Id; Persist();
   } catch(Exception original) {
    g.Head=oldHead;
    if(modifying) {
     try {
      var originalFiles=safety==null?new List<SaveFile>():safety.Files;
      var originalTargets=new HashSet<string>(originalFiles.Select(f=>Target(g,f.Entry)),StringComparer.OrdinalIgnoreCase);
      foreach(var target in targets) if(!originalTargets.Contains(target)&&File.Exists(target)) File.Delete(target);
      if(safety!=null) using(var rollback=ZipFile.OpenRead(Path.Combine(Folder(g.Id),safety.Id,"save.zip"))) foreach(var f in safety.Files) {
       var target=Target(g,f.Entry);
       if(File.Exists(target)&&HashFile(target)==f.Hash) continue;
       Directory.CreateDirectory(Path.GetDirectoryName(target)); var temp=target+".rollback-"+Guid.NewGuid().ToString("N");
       try { rollback.GetEntry(f.Entry).ExtractToFile(temp); if(File.Exists(target)) File.Replace(temp,target,null); else File.Move(temp,target); }
       finally { if(File.Exists(temp)) File.Delete(temp); }
      }
     } catch(Exception rollbackError) { throw new IOException("恢复失败且自动回滚未完全完成。请保留恢复前备份 "+(safety==null?"（原存档为空）":safety.Id)+"。错误："+original.Message+"；回滚："+rollbackError.Message); }
     throw new IOException("恢复未完成，已回滚原文件。"+original.Message,original);
    }
    throw;
   } finally { if(Directory.Exists(stage)) Directory.Delete(stage,true); }
  }
  public string Target(Game g,string entry) {
   var split=entry.IndexOf('/'); if(split<=0) throw new InvalidDataException("备份条目无效。");
   var id=entry.Substring(0,split); var rest=entry.Substring(split+1); var r=g.Rules.SingleOrDefault(x=>x.Id==id);
   if(r==null||string.IsNullOrWhiteSpace(rest)||rest.Contains("\\")||rest.Split('/').Any(x=>x==".."||x=="."||x.Length==0||x.Contains(":"))) throw new InvalidDataException("备份路径不安全或本机位置未绑定。");
   var p=Paths.Resolve(r); if(r.IsFile) { if(rest!="file") throw new InvalidDataException("文件映射错误。"); return p; }
   return Paths.Within(p,rest.Replace('/','\\'));
  }
  public void Publish(Game g,Snapshot s) {
   Guid parsed; if(!Guid.TryParseExact(s.Id,"N",out parsed)) throw new InvalidDataException("版本标识无效。");
   var source=Path.Combine(Folder(g.Id),s.Id); if(HashFile(Path.Combine(source,"save.zip"))!=s.ZipHash) throw new InvalidDataException("本机备份包损坏。"); Verify(Path.Combine(source,"save.zip"),s);
   Publish(new FolderStorage(State.Library),g,s,source);
  }
  public void Publish(IStorageProvider store,Game g,Snapshot s,string source) {
   store.Publish(g,s,source);
   g.LastSynced=s.Id; Persist();
  }
  public int Fetch() {
   return Fetch(new FolderStorage(State.Library));
  }
  public int Fetch(IStorageProvider store) {
   var count=0;
   foreach(var imported in store.Games()) {
    Folder(imported.Id);
    var game=State.Games.FirstOrDefault(x=>x.Id==imported.Id);
    if(game==null) { game=imported; State.Games.Add(game); }
    foreach(var s in store.Versions(imported)) {
     Guid parsed; if(s.GameId!=game.Id||!Guid.TryParseExact(s.Id,"N",out parsed)) throw new InvalidDataException("同步库版本标识错误。");
     var dest=Path.Combine(Folder(game.Id),s.Id);
     if(Directory.Exists(dest)) { if(HashFile(Path.Combine(dest,"save.zip"))!=s.ZipHash) throw new InvalidDataException("本机同名版本内容不同。"); continue; }
     var temp=Path.Combine(Data,"staging",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
     try { store.Download(s,temp); var zipPath=Path.Combine(temp,"save.zip"); if(HashFile(zipPath)!=s.ZipHash) throw new InvalidDataException("同步库备份包损坏。"); Verify(zipPath,s); Json.Write(Path.Combine(temp,"manifest.json"),s); Directory.CreateDirectory(Folder(game.Id)); Directory.Move(temp,dest); count++; }
     finally { if(Directory.Exists(temp)) Directory.Delete(temp,true); }
    }
   }
   Persist(); return count;
  }
 }
 public class MainWindow : Window {
  Engine engine; ListBox games=new ListBox(), versions=new ListBox(); TextBlock summary=new TextBlock(), status=new TextBlock(); StackPanel paths=new StackPanel(); bool busy;
  CheckBox autoBackup=new CheckBox { Content="游戏退出后自动备份",Foreground=Ink,Margin=new Thickness(0,8,0,8) }; bool refreshing,exiting;
  System.Windows.Forms.NotifyIcon tray; System.Windows.Threading.DispatcherTimer monitor;
  Dictionary<string,bool> running=new Dictionary<string,bool>(); Dictionary<string,int> pending=new Dictionary<string,int>();
  static Brush Ink=new SolidColorBrush(Color.FromRgb(226,232,240)), Muted=new SolidColorBrush(Color.FromRgb(148,163,184)), PanelColor=new SolidColorBrush(Color.FromRgb(25,34,51));
  public MainWindow(Engine e) {
   engine=e; Title="存档港 · SaveHarbor"; Width=1100; Height=760; MinWidth=920; MinHeight=650; Background=new SolidColorBrush(Color.FromRgb(12,19,32)); Foreground=Ink; FontFamily=new FontFamily("Microsoft YaHei UI"); FontSize=14;
   var root=new DockPanel { Margin=new Thickness(28) }; Content=root;
   var header=new StackPanel { Margin=new Thickness(0,0,0,24) }; header.Children.Add(new TextBlock { Text="存档港",FontSize=30,FontWeight=FontWeights.Bold }); header.Children.Add(new TextBlock { Text="把每一次冒险，留在正确的位置。",Foreground=Muted,Margin=new Thickness(0,8,0,0) }); DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
   var foot=new StackPanel { Margin=new Thickness(0,16,0,0) }; status.Text="就绪 · 本地版本 0.1"; status.TextWrapping=TextWrapping.Wrap; foot.Children.Add(status); foot.Children.Add(new TextBlock { Text="百度扫码：尚未接入  ·  文件夹存档库需要外部网盘客户端或共享目录传输",Foreground=Muted,FontSize=12,Margin=new Thickness(0,8,0,0) }); DockPanel.SetDock(foot,Dock.Bottom); root.Children.Add(foot);
   var grid=new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(260) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(grid);
   var left=new DockPanel { Margin=new Thickness(0,0,24,0) }; grid.Children.Add(left);
   var tools=new StackPanel(); tools.Children.Add(Button("＋ 添加游戏",AddGame)); tools.Children.Add(Button("设置文件夹存档库",SetLibrary)); tools.Children.Add(Button("从存档库拉取",delegate { Run(async delegate { var n=await Task.Run(()=>engine.Fetch()); Reload(); Say("已导入 "+n+" 个版本；尚未覆盖任何游戏存档。"); }); })); DockPanel.SetDock(tools,Dock.Top); left.Children.Add(tools);
   games.Background=PanelColor; games.Foreground=Ink; games.BorderThickness=new Thickness(0); games.SelectionChanged+=delegate { Details(); }; left.Children.Add(games);
   var right=new DockPanel(); Grid.SetColumn(right,1); grid.Children.Add(right);
   var top=new StackPanel(); summary.FontSize=18; summary.TextWrapping=TextWrapping.Wrap; summary.Margin=new Thickness(0,0,0,16); top.Children.Add(summary);
   var row=new WrapPanel(); row.Children.Add(Button("编辑路径 / EXE",EditGame)); row.Children.Add(Button("分析存档",delegate { Run(async delegate { var g=Selected(); var files=await Task.Run(()=>engine.Scan(g)); var fp=Engine.Fingerprint(files); var previous=engine.Versions(g).FirstOrDefault(); Say(files.Count+" 个文件 · "+Size(files.Sum(x=>x.Size))+" · "+(previous!=null&&fp==previous.Fingerprint?"与最新本地版本内容一致":"尚未备份的内容")+"。云端冲突需查看版本来源与父版本。"); }); }));
   row.Children.Add(Button("创建备份",delegate { Run(async delegate { var g=Selected(); engine.EnsureGameClosed(g); var s=await Task.Run(()=>engine.Backup(g,"手动备份",g.Head)); g.Head=s.Id; engine.Persist(); Details(); Say("备份完成："+Size(s.Files.Sum(x=>x.Size))+" → "+Size(new FileInfo(Path.Combine(engine.Folder(g.Id),s.Id,"save.zip")).Length)); }); })); top.Children.Add(row);
   row.Children.Add(Button("启动游戏",delegate { var g=Selected(); if(!File.Exists(g.Exe)) throw new IOException("请先绑定这台电脑的游戏 EXE。"); Process.Start(new ProcessStartInfo(g.Exe) { WorkingDirectory=Path.GetDirectoryName(g.Exe),UseShellExecute=true }); Say("游戏已启动。若启用自动备份，退出游戏后会创建本地版本。开始前可手动拉取和恢复存档库版本。"); }));
   autoBackup.Click+=delegate { if(refreshing||busy) return; var g=games.SelectedItem as Game; if(g==null) return; if(autoBackup.IsChecked==true&&string.IsNullOrWhiteSpace(g.Exe)) { autoBackup.IsChecked=false; Say("请先设置游戏 EXE。"); return; } g.AutoBackup=autoBackup.IsChecked==true; engine.Persist(); }; top.Children.Add(autoBackup);
   paths.Margin=new Thickness(0,12,0,12); top.Children.Add(paths); DockPanel.SetDock(top,Dock.Top); right.Children.Add(top);
   var actions=new WrapPanel { Margin=new Thickness(0,12,0,0) }; actions.Children.Add(Button("恢复选中版本",Restore)); actions.Children.Add(Button("发布选中版本到存档库",delegate { Run(async delegate { var g=Selected(); var s=Version(); await Task.Run(()=>engine.Publish(g,s)); Say("版本已发布到文件夹存档库；外部客户端是否上传完成需另行确认。"); }); })); actions.Children.Add(Button("打开本机备份目录",delegate { var g=Selected(); Directory.CreateDirectory(engine.Folder(g.Id)); Process.Start(new ProcessStartInfo(engine.Folder(g.Id)) { UseShellExecute=true }); })); DockPanel.SetDock(actions,Dock.Bottom); right.Children.Add(actions);
   var vpanel=new DockPanel(); var label=new TextBlock { Text="版本时间线  ·  相同内容不代表相同进度分支",Foreground=Muted,Margin=new Thickness(0,0,0,10) }; DockPanel.SetDock(label,Dock.Top); vpanel.Children.Add(label); versions.Background=PanelColor; versions.Foreground=Ink; versions.BorderThickness=new Thickness(0); versions.SelectionChanged+=delegate { var s=versions.SelectedItem as Snapshot; if(s!=null) Say("版本 "+s.Id.Substring(0,8)+" · 父版本 "+(s.Parent.Length>0?s.Parent.Substring(0,8):"无")+" · "+s.Files.Count+" 文件 · "+Size(s.Files.Sum(x=>x.Size))); }; vpanel.Children.Add(versions); right.Children.Add(vpanel);
   Reload();
  }
  public void EnableBackground() {
   tray=new System.Windows.Forms.NotifyIcon { Icon=System.Drawing.SystemIcons.Application,Text="存档港 · SaveHarbor",Visible=true };
   var menu=new System.Windows.Forms.ContextMenuStrip(); menu.Items.Add("打开存档港",null,delegate { Dispatcher.BeginInvoke(new Action(delegate { Show(); WindowState=WindowState.Normal; Activate(); })); });
   menu.Items.Add("退出程序",null,delegate { Dispatcher.BeginInvoke(new Action(delegate { if(busy) { Say("操作进行中，请完成后再退出。"); Show(); return; } exiting=true; Close(); })); }); tray.ContextMenuStrip=menu;
   tray.DoubleClick+=delegate { Dispatcher.BeginInvoke(new Action(delegate { Show(); WindowState=WindowState.Normal; Activate(); })); };
   Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs args) { if(!exiting) { args.Cancel=true; Hide(); tray.ShowBalloonTip(2000,"存档港","已在后台运行。右键托盘图标可退出。",System.Windows.Forms.ToolTipIcon.Info); } };
   Closed+=delegate { monitor.Stop(); tray.Dispose(); };
   monitor=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(3) };
   monitor.Tick+=delegate {
    foreach(var g in engine.State.Games.ToList()) {
     if(!g.AutoBackup||string.IsNullOrWhiteSpace(g.Exe)) continue;
     var active=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(g.Exe)).Length>0; bool before;
     if(running.TryGetValue(g.Id,out before)&&before&&!active) pending[g.Id]=0; running[g.Id]=active;
    }
    if(busy||pending.Count==0) return;
    var id=pending.Keys.First(); var game=engine.State.Games.FirstOrDefault(x=>x.Id==id);
    if(game==null||!game.AutoBackup) { pending.Remove(id); return; }
    if(running.ContainsKey(id)&&running[id]) return;
    Run(async delegate {
     try {
      var files=await Task.Run(()=>engine.Scan(game)); var head=engine.Versions(game).FirstOrDefault(x=>x.Id==game.Head);
      if(head!=null&&head.Fingerprint==Engine.Fingerprint(files)) { pending.Remove(id); Say(game.Name+"：内容未变化，无需新备份。"); return; }
      engine.EnsureGameClosed(game); var snap=await Task.Run(()=>engine.Backup(game,"游戏退出自动备份",game.Head)); game.Head=snap.Id; engine.Persist(); pending.Remove(id); Details(); Say(game.Name+"：退出后的本地备份完成，尚未发布到存档库。");
      tray.ShowBalloonTip(2500,"备份完成",game.Name+" 的存档已保留到本机。",System.Windows.Forms.ToolTipIcon.Info);
     } catch { pending[id]++; if(pending[id]>=3) pending.Remove(id); throw; }
    });
   }; monitor.Start();
  }
  public static string Size(long n) { return n>=1073741824?(n/1073741824d).ToString("0.00")+" GB":n>=1048576?(n/1048576d).ToString("0.00")+" MB":(n/1024d).ToString("0.0")+" KB"; }
  Button Button(string text,RoutedEventHandler action) { var b=new Button { Content=text,Padding=new Thickness(14,9,14,9),Margin=new Thickness(0,0,8,8),Background=new SolidColorBrush(Color.FromRgb(42,57,79)),Foreground=Ink,BorderThickness=new Thickness(0) }; b.Click+=delegate(object sender,RoutedEventArgs args) { if(busy) return; try { action(sender,args); } catch(Exception ex) { Say(ex.Message); } }; return b; }
  async void Run(Func<Task> action) { if(busy) return; busy=true; ((UIElement)Content).IsEnabled=false; Say("处理中，请稍候…"); try { await action(); } catch(Exception ex) { Say("操作未完成："+ex.Message); } finally { busy=false; ((UIElement)Content).IsEnabled=true; } }
  void Say(string text) { status.Text=text; }
  Game Selected() { var g=games.SelectedItem as Game; if(g==null) throw new InvalidOperationException("请先选择一个游戏。"); return g; }
  Snapshot Version() { var s=versions.SelectedItem as Snapshot; if(s==null) throw new InvalidOperationException("请先选择一个版本。"); return s; }
  void Reload() { var selected=games.SelectedItem as Game; games.ItemsSource=null; games.ItemsSource=engine.State.Games; games.SelectedItem=selected??engine.State.Games.FirstOrDefault(); Details(); }
  void Details() {
   var g=games.SelectedItem as Game; paths.Children.Clear(); versions.ItemsSource=null;
   refreshing=true; autoBackup.IsChecked=g!=null&&g.AutoBackup; autoBackup.IsEnabled=g!=null; refreshing=false;
   if(g==null) { summary.Text="添加第一款游戏，建立你的存档库。"; return; }
   summary.Text=g.Name+"\n"+(string.IsNullOrEmpty(g.Exe)?"需要设置游戏 EXE，才能执行备份与恢复":"游戏进程："+Path.GetFileName(g.Exe));
   foreach(var r in g.Rules) { string actual; try { actual=Paths.Resolve(r); } catch { actual="本机根目录待绑定"; } paths.Children.Add(new TextBlock { Text="{"+r.Anchor+"}\\"+r.Relative+"\n"+actual,Foreground=Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8) }); }
   versions.ItemsSource=engine.Versions(g);
  }
  void AddGame(object o,RoutedEventArgs a) { var g=new Game(); if(new EditWindow(g).ShowDialog()==true) { engine.State.Games.Add(g); engine.Persist(); Reload(); games.SelectedItem=g; } }
  void EditGame(object o,RoutedEventArgs a) { var original=Selected(); var clone=new Game { Id=original.Id,Name=original.Name,Exe=original.Exe,LastSynced=original.LastSynced,Head=original.Head,AutoBackup=original.AutoBackup,Rules=original.Rules.Select(r=>new Rule { Id=r.Id,Anchor=r.Anchor,Relative=r.Relative,Root=r.Root,IsFile=r.IsFile }).ToList() }; if(new EditWindow(clone).ShowDialog()==true) { engine.State.Games[engine.State.Games.IndexOf(original)]=clone; engine.Persist(); Reload(); games.SelectedItem=clone; } }
  void SetLibrary(object o,RoutedEventArgs a) { var p=Prompt("文件夹存档库","输入一个已有文件夹路径。可选百度网盘客户端同步目录、共享目录或移动磁盘。",engine.State.Library); if(p==null) return; p=Path.GetFullPath(p.Trim().Trim('"')); if(!Directory.Exists(p)) throw new DirectoryNotFoundException("请先创建这个目录。"); Paths.NoLinks(p); if(p.Equals(engine.Data,StringComparison.OrdinalIgnoreCase)||p.StartsWith(engine.Data.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||engine.Data.StartsWith(p.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("同步库不能与本机数据目录重叠。"); engine.State.Library=p; engine.Persist(); Say("文件夹存档库："+p); }
  void Restore(object o,RoutedEventArgs a) { var g=Selected(); var s=Version(); if(MessageBox.Show("将恢复选中版本到已配置的位置。当前存档会先备份；范围内多余文件会在恢复后移除。\n请确认游戏已关闭。","恢复存档",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK) return; Run(async delegate { await Task.Run(()=>engine.Restore(g,s)); Details(); Say("恢复完成；恢复前的存档已保留在版本时间线中（如本机此前存在存档）。"); }); }
  public static string Prompt(string title,string description,string initial) { var w=new Window { Title=title,Width=600,Height=230,WindowStartupLocation=WindowStartupLocation.CenterScreen,ResizeMode=ResizeMode.NoResize }; var panel=new StackPanel { Margin=new Thickness(20) }; w.Content=panel; panel.Children.Add(new TextBlock { Text=description,TextWrapping=TextWrapping.Wrap }); var t=new TextBox { Text=initial??"",Margin=new Thickness(0,16,0,16),Padding=new Thickness(8) }; panel.Children.Add(t); var b=new Button { Content="确定",Padding=new Thickness(12,8,12,8),IsDefault=true }; b.Click+=delegate { w.DialogResult=true; }; panel.Children.Add(b); return w.ShowDialog()==true?t.Text:null; }
 }
 public class EditWindow : Window {
  public EditWindow(Game game) {
   Title="建立游戏档案 / 绑定本机路径"; Width=740; Height=620; WindowStartupLocation=WindowStartupLocation.CenterScreen; var panel=new StackPanel { Margin=new Thickness(24) }; Content=new ScrollViewer { Content=panel };
   panel.Children.Add(new TextBlock { Text="游戏名称" }); var name=new TextBox { Text=game.Name,Margin=new Thickness(0,8,0,16) }; panel.Children.Add(name);
   panel.Children.Add(new TextBlock { Text="游戏 EXE 路径（用于检查进程；新电脑需重新设置）" }); var exe=new TextBox { Text=game.Exe,Margin=new Thickness(0,8,0,16) }; panel.Children.Add(exe);
   panel.Children.Add(new TextBlock { Text="存档位置 · 支持多个文件或目录",FontWeight=FontWeights.Bold });
   var rules=game.Rules.Select(r=>new Rule { Id=r.Id,Anchor=r.Anchor,Root=r.Root,Relative=r.Relative,IsFile=r.IsFile }).ToList(); var list=new ListBox { Height=200,Margin=new Thickness(0,12,0,12) }; panel.Children.Add(list);
   Action refresh=delegate { list.Items.Clear(); foreach(var r in rules) list.Items.Add("{"+r.Anchor+"}\\"+r.Relative+(r.IsFile?" [文件]":" [目录]")+" · 本机根："+r.Root); }; refresh();
   var buttons=new WrapPanel(); panel.Children.Add(buttons);
   Action<string,RoutedEventHandler> add=delegate(string text,RoutedEventHandler action) { var b=new Button { Content=text,Margin=new Thickness(0,0,10,10),Padding=new Thickness(10) }; b.Click+=delegate(object sender,RoutedEventArgs args) { try { action(sender,args); } catch(Exception ex) { MessageBox.Show(ex.Message); } }; buttons.Children.Add(b); };
   add("添加文件夹",delegate { var p=MainWindow.Prompt("存档位置","粘贴存档目录的完整路径，软件将自动转换位置模板。",""); if(p==null) return; var r=Paths.From(p); if(r.IsFile) throw new IOException("请选择目录。"); rules.Add(r); refresh(); });
   add("添加文件",delegate { var d=new Microsoft.Win32.OpenFileDialog(); if(d.ShowDialog()==true) { rules.Add(Paths.From(d.FileName)); refresh(); } });
   add("修改模板 / 绑定根目录",delegate { if(list.SelectedIndex<0) return; var r=rules[list.SelectedIndex]; var a=MainWindow.Prompt("基准位置","可用：Documents、LocalAppData、AppData、UserProfile、GameRoot、CustomRoot",r.Anchor); if(a==null) return; if(!new[]{"Documents","LocalAppData","AppData","UserProfile","GameRoot","CustomRoot"}.Contains(a)) throw new IOException("基准位置无效。"); var rel=MainWindow.Prompt("相对路径","输入相对基准位置的路径，不能包含盘符或越出根目录。",r.Relative); if(rel==null) return; var root=r.Root; if(a=="GameRoot"||a=="CustomRoot") { root=MainWindow.Prompt("本机根目录","输入这台电脑上的根目录。换设备后可重新绑定。",root); if(root==null) return; } var updated=new Rule { Id=r.Id,Anchor=a,Root=root,Relative=rel,IsFile=r.IsFile }; Paths.Resolve(updated); rules[list.SelectedIndex]=updated; refresh(); });
   add("移除位置",delegate { if(list.SelectedIndex>=0) { rules.RemoveAt(list.SelectedIndex); refresh(); } });
   panel.Children.Add(new TextBlock { Text="请选择真正的存档范围，避免包含整个用户目录、游戏安装文件、缓存或截图。首版以手动操作为主，备份前与恢复前会检查所填 EXE 的进程名称。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,16) });
   var save=new Button { Content="保存档案",Padding=new Thickness(12),IsDefault=true }; save.Click+=delegate { try { if(string.IsNullOrWhiteSpace(name.Text)||rules.Count==0) throw new IOException("请输入游戏名称并添加存档位置。"); if(!string.IsNullOrWhiteSpace(exe.Text)&&(!File.Exists(exe.Text.Trim().Trim('"'))||!exe.Text.Trim().Trim('"').EndsWith(".exe",StringComparison.OrdinalIgnoreCase))) throw new IOException("游戏 EXE 路径无效。"); game.Name=name.Text.Trim(); game.Exe=exe.Text.Trim().Trim('"'); game.Rules=rules; DialogResult=true; } catch(Exception ex) { MessageBox.Show(ex.Message); } }; panel.Children.Add(save);
  }
 }
 public static class Program {
  [STAThread] public static int Main(string[] args) {
   try {
    if(args.Length>0&&args[0]=="--self-test") { Tests.Run(args[1]); return 0; }
    var data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SaveHarbor");
    if(args.Length>=2&&args[0]=="--data") data=args[1];
    if(args.Length>=3&&args[0]=="--preview") data=args[2];
    var app=new Application(); var e=new Engine(data); var window=new MainWindow(e);
    if(args.Length>=3&&args[0]=="--preview") {
     // Preview uses only caller-supplied workspace data, never the real profile.
     var content=(UIElement)window.Content; window.Content=null; var scene=new Border { Background=window.Background,Child=content };
     System.Windows.Documents.TextElement.SetForeground(scene,window.Foreground); System.Windows.Documents.TextElement.SetFontFamily(scene,window.FontFamily); System.Windows.Documents.TextElement.SetFontSize(scene,window.FontSize);
     scene.Measure(new Size(1100,760)); scene.Arrange(new Rect(0,0,1100,760)); scene.UpdateLayout();
     var bmp=new RenderTargetBitmap(1100,760,96,96,PixelFormats.Pbgra32); bmp.Render(scene); var enc=new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using(var f=File.Create(args[1])) enc.Save(f); return 0;
    }
    using(var mutex=new System.Threading.Mutex(false,"Local\\SaveHarbor-"+Engine.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToLowerInvariant())))) {
     bool acquired; try { acquired=mutex.WaitOne(0); } catch(System.Threading.AbandonedMutexException) { acquired=true; }
     if(!acquired) { MessageBox.Show("这个存档港数据目录已由另一个实例使用。请从托盘打开已有窗口。"); return 0; }
     try { window.EnableBackground(); app.Run(window); } finally { mutex.ReleaseMutex(); }
    }
    return 0;
   } catch(Exception ex) { if(args.Length>0&&args[0]=="--self-test") { File.WriteAllText(Path.Combine(args[1],"FAILED.txt"),ex.ToString()); return 1; } MessageBox.Show(ex.Message,"存档港"); return 1; }
  }
 }
 public static class Tests {
  public static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
  public static void Run(string root) {
   root=Path.GetFullPath(root); Directory.CreateDirectory(root); var saves=Path.Combine(root,"game-save"); Directory.CreateDirectory(saves); File.WriteAllText(Path.Combine(saves,"slot.dat"),"progress-A");
   var e=new Engine(Path.Combine(root,"device-A")); var g=new Game { Name="测试游戏",Exe="SaveHarbor-Test-Game.exe",Rules=new List<Rule> { new Rule { Anchor="CustomRoot",Root=root,Relative="game-save" } } }; e.State.Games.Add(g); e.Persist();
   var a=e.Backup(g,"A",""); Check(a.Files.Count==1,"文件清单"); File.WriteAllText(Path.Combine(saves,"slot.dat"),"progress-B"); File.WriteAllText(Path.Combine(saves,"extra.dat"),"extra");
   e.Restore(g,a); Check(File.ReadAllText(Path.Combine(saves,"slot.dat"))=="progress-A","恢复内容"); Check(!File.Exists(Path.Combine(saves,"extra.dat")),"恢复移除额外文件"); Check(e.Versions(g).Count==2,"恢复前备份");
   var lib=Path.Combine(root,"library"); Directory.CreateDirectory(lib); e.State.Library=lib; e.Publish(g,a);
   var b=new Engine(Path.Combine(root,"device-B")); b.State.Library=lib; Check(b.Fetch()==1,"设备 B 拉取"); Check(b.State.Games[0].Rules[0].Root=="","本机路径不应发布"); Check(b.State.Games[0].Exe=="","EXE 不应发布");
   var other=Path.Combine(root,"another-device-root"); Directory.CreateDirectory(other); b.State.Games[0].Rules[0].Root=other; b.State.Games[0].Exe=g.Exe; b.Restore(b.State.Games[0],b.Versions(b.State.Games[0])[0]); Check(File.ReadAllText(Path.Combine(other,"game-save","slot.dat"))=="progress-A","新电脑路径重绑定");
   var blocked=false; try { Paths.Within(root,"..\\escape"); } catch(InvalidDataException) { blocked=true; } Check(blocked,"路径穿越");
   var malformed=new Snapshot { GameId=g.Id }; malformed.Files.Add(new SaveFile { Entry=g.Rules[0].Id+"/../escape",Size=1,Hash="x" }); blocked=false; try { e.Target(g,malformed.Files[0].Entry); } catch(InvalidDataException) { blocked=true; } Check(blocked,"归档路径穿越");
   var zipPath=Path.Combine(e.Folder(g.Id),a.Id,"save.zip"); var bytes=File.ReadAllBytes(zipPath); bytes[0]^=255; File.WriteAllBytes(zipPath,bytes); blocked=false; try { e.Restore(g,a); } catch(InvalidDataException) { blocked=true; } Check(blocked,"损坏备份拦截"); Check(File.ReadAllText(Path.Combine(saves,"slot.dat"))=="progress-A","损坏备份不覆盖"); File.WriteAllBytes(zipPath,bytes.Select((x,i)=>i==0?(byte)(x^255):x).ToArray());
   // A restore that encounters a locked extra file must undo files it already changed.
   File.WriteAllText(Path.Combine(saves,"slot.dat"),"before-failed-restore"); File.WriteAllText(Path.Combine(saves,"locked.dat"),"locked-original");
   using(var locked=File.Open(Path.Combine(saves,"locked.dat"),FileMode.Open,FileAccess.Read,FileShare.Read)) {
    blocked=false; try { e.Restore(g,a); } catch(IOException) { blocked=true; } Check(blocked,"锁定文件导致恢复失败");
   }
   Check(File.ReadAllText(Path.Combine(saves,"slot.dat"))=="before-failed-restore","失败恢复回滚已有文件"); Check(File.ReadAllText(Path.Combine(saves,"locked.dat"))=="locked-original","失败恢复保留原额外文件");
   Check(!Directory.GetFiles(saves,"*.saveharbor-*").Any(),"恢复临时文件清理");
   var childA=e.Backup(g,"设备 A 分支",a.Id); e.Publish(g,childA);
   File.WriteAllText(Path.Combine(other,"game-save","slot.dat"),"device-B-progress");
   var childB=b.Backup(b.State.Games[0],"设备 B 分支",a.Id); b.Publish(b.State.Games[0],childB);
   e.Fetch(); Check(e.Versions(g).Any(x=>x.Id==childA.Id)&&e.Versions(g).Any(x=>x.Id==childB.Id),"同时保留不同设备分支");
   var currentExe=g.Exe; g.Exe=Process.GetCurrentProcess().ProcessName+".exe"; blocked=false; try { e.EnsureGameClosed(g); } catch(InvalidOperationException) { blocked=true; } Check(blocked,"运行中的游戏进程拦截"); g.Exe=currentExe;
   var singlePath=Path.Combine(root,"single.dat"); File.WriteAllText(singlePath,"single-A"); var single=new Game { Name="单文件测试",Exe=currentExe,Rules=new List<Rule> { new Rule { Anchor="CustomRoot",Root=root,Relative="single.dat",IsFile=true } } }; var singleVersion=e.Backup(single,"单文件",""); File.WriteAllText(singlePath,"single-B"); e.Restore(single,singleVersion); Check(File.ReadAllText(singlePath)=="single-A","单文件恢复");
   var manifestTraversal=new Snapshot { Id="..",GameId=g.Id }; blocked=false; try { e.Restore(g,manifestTraversal); } catch(InvalidDataException) { blocked=true; } Check(blocked,"版本标识穿越拦截");
   var largeDir=Path.Combine(root,"large-save"); Directory.CreateDirectory(largeDir); var largeBytes=new byte[12*1024*1024]; new Random(42).NextBytes(largeBytes); File.WriteAllBytes(Path.Combine(largeDir,"world.dat"),largeBytes);
   var large=new Game { Name="大存档测试",Exe=currentExe,Rules=new List<Rule> { new Rule { Anchor="CustomRoot",Root=root,Relative="large-save" } } }; var largeVersion=e.Backup(large,"12 MB",""); Check(largeVersion.Files.Sum(x=>x.Size)>10*1024*1024,"超过 10 MB 的存档");
   File.WriteAllText(Path.Combine(root,"PASSED.txt"),"PASS: backup; verified restore; safety backup; extra-file removal; portable catalog; new-device path mapping; traversal rejection; corrupted archive rejection; failure rollback; temporary-file cleanup; concurrent device branches; active-process block; single-file restore; manifest-ID rejection; 12 MB archive.");
  }
 }
}
