using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using RniPanel;

sealed class CatalogFixture {
    public string Database {get;set;}
    public string MultiVariantFile {get;set;}
    public string MultiVariantUuid {get;set;}
    public string UniqueFile {get;set;}
    public int MultiVariantIndex {get;set;}
    public int MultiVariantId {get;set;}
    public int UniqueId {get;set;}
}
sealed class FakeLiveSession:ILiveStyleSession {
    public bool Current=true,SendFails,KeepOldStyle;
    public string[] Active;
    public string Requested;
    public int Validations,Reads,Sends;
    public Action<FakeLiveSession> OnValidate,OnRead,OnSend;
    public bool IsCurrent {get{return Current;}}
    public Task ValidateTarget() {
        Validations++;if(OnValidate!=null)OnValidate(this);return Task.FromResult(0);
    }
    public Task<string[]> ReadAppliedStyles() {
        Reads++;if(OnRead!=null)OnRead(this);return Task.FromResult(Active);
    }
    public Task SendShortcut() {
        Sends++;if(OnSend!=null)OnSend(this);
        if(SendFails)throw new InvalidOperationException("incomplete input, outcome unknown");
        if(!KeepOldStyle)Active=new[]{Requested};return Task.FromResult(0);
    }
}
sealed class FakeClearSession:IClearStyleSession {
    public bool IsCurrent {get;set;}
    public string[] Active;
    public int Sends;
    public FakeClearSession(){IsCurrent=true;}
    public Task ValidateTarget(){return Task.FromResult(0);}
    public Task<string[]> ReadAppliedStyles(){return Task.FromResult(Active);}
    public Task RemoveCurrentStyle(string identity){Sends++;Active=Active.Where(s=>s!=identity).ToArray();return Task.FromResult(0);}
}
sealed class FakeBatchSession:IBatchStyleSession {
    public bool IsCurrent {get;set;}
    public TargetSnapshot[] Items;
    public int Index=1,Sends;
    public bool PrimaryOnly,ModeRestored;
    public int FailOnItem=-1;
    public FakeBatchSession() {
        IsCurrent=true;
        Items=Enumerable.Range(1,3).Select(i=>new TargetSnapshot{Handle=new IntPtr(1),ProcessId=1,ProcessStartTicks=1,
            Title="RNI-Panel-Sandbox",DocumentPath=SafetyPolicy.DemoCatalogPath,VariantId=i,VariantUuid="identity-"+i,
            FileName="photo-"+i,SelectedCount=3,Enabled=true}).ToArray();
    }
    public Task<TargetSnapshot> InspectPrimary(){return Task.FromResult(Items[Index]);}
    public Task BeginPrimaryOnly(){PrimaryOnly=true;return Task.FromResult(0);}
    public Task SelectFirst(){Index=0;return Task.FromResult(0);}
    public Task SelectNext(){Index=Math.Min(Index+1,Items.Length-1);return Task.FromResult(0);}
    public Task<bool> ExecutePrimary(TargetSnapshot expected) {
        if(!PrimaryOnly||expected!=Items[Index])throw new InvalidOperationException("unsafe target");
        if(Index==FailOnItem)throw new InvalidOperationException("native confirmation failed");
        Sends++;return Task.FromResult(true);
    }
    public Task RestoreEditMode(){PrimaryOnly=false;ModeRestored=true;return Task.FromResult(0);}
}
static class Tests {
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); count++; }
    static void Throws(Action action, string name) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, name); }
    static void Throws<T>(Action action,string name) where T:Exception {
        try{action();}catch(T){Check(true,name);return;}throw new Exception("FAIL expected "+typeof(T).Name+": "+name);
    }
    public static int Main(string[] args) {
        string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "RniPanelTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string appRoot = @"C:\Program Files\Capture One\Capture One";
        var catalog = Catalog.Read(Catalog.FindRoots(appRoot), appRoot);
        Check(catalog.FileCount > 0 && catalog.Families.Count > 0, "installed styles indexed read-only");
        Check(catalog.Families.All(f => f.Styles.Select(s=>s.Strength).Distinct().Count()==f.Styles.Count), "no duplicate intensity within a family");
        Check(catalog.Families.Any(f=>f.Matches("portra160")), "spaceless film search");
        var allBindings = Shortcuts.CreateBindings(catalog);
        Check(allBindings.Count==catalog.Families.Sum(f=>f.Styles.Count(s=>s.IccExists)),"every installed usable style has an execution binding");
        Check(allBindings.Select(b=>b.Style.Uuid).Distinct().Count()==allBindings.Count,"all source UUID identities remain distinct");
        Check(allBindings.All(b=>b.NativePath!=null&&b.NativePath.Length>=3&&b.NativePath.Last()==b.Style.NativeName),"native tree leaf uses the exact filename stem, not the XML name");
        var differentNames=allBindings.Where(b=>b.Style.Name!=b.Style.NativeName).ToArray();
        Check(differentNames.Length>0&&differentNames.All(b=>b.NativePath.Last()==Path.GetFileNameWithoutExtension(b.Style.Path)&&
            b.Style.MatchesAppliedName(b.Style.Name)&&b.Style.MatchesAppliedName(b.Style.NativeName)),"all installed name mismatches use the same exact two-name mapping, not a Portra whitelist");
        foreach(var naming in new[]{new[]{"Kodak Portra 160 V.5","Kodak Portra 160 v5"},new[]{"Kodak Portra 400 V.2","Kodak Portra 400 v2"}}) {
            var family=catalog.Families.Single(f=>f.Name==naming[0]&&!f.Grain&&!f.Rendered);
            Check(new[]{25,50,75,100}.All(level=>family.At(level).Name==naming[0]+" "+level+"%"&&
                family.At(level).NativeName==naming[1]+" "+level+"%"&&family.At(level).NativePath.Last()==naming[1]+" "+level+"%"),"versioned Portra all four native leaves: "+naming[1]);
            var versioned=family.At(100);
            Check(versioned.MatchesAppliedName(naming[0]+" 100%")&&versioned.MatchesAppliedName(naming[1]+" 100%"),"versioned Portra readback accepts its two exact labels: "+naming[1]);
            Check(!versioned.MatchesAppliedName(naming[1]+"0 100%")&&!versioned.MatchesAppliedName(naming[1]+" HC 100%"),"native aliases do not blur other versions or HC variants: "+naming[1]);
            var legacyKey=Catalog.Normalize(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(versioned.Path)))+"|"+
                Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(versioned.Path)))+"|"+family.Name);
            Check(family.Id==Catalog.HashText(legacyKey).Substring(0,24),"XML family/favorite ID stays unchanged: "+naming[1]);
            Check(family.Matches(naming[0])&&family.Matches(naming[1]),"search accepts XML and native version spelling: "+naming[1]);
        }
        var natura=catalog.Families.Single(f=>f.Name=="Fuji Natura 1600"&&!f.Grain&&!f.Rendered).At(100);
        Check(natura.NativeName==natura.Name&&natura.AppliedNames.Count()==1,"accepted Natura naming is unchanged");
        Check(allBindings.GroupBy(b=>b.Style.Name).Where(g=>g.Count()>1).All(g=>g.Select(b=>String.Join("/",b.NativePath)).Distinct().Count()==g.Count()),"same-name standard/grain entries have different native paths");
        var nativeOnly=Shortcuts.CreateBindings(catalog,null);
        Check(nativeOnly.Count==allBindings.Count&&nativeOnly.All(b=>b.UsesNativeTree),"full catalog does not require a demo shortcut set");
        var bindings=allBindings.Where(b=>b.Shortcut!=0).ToList();
        Check(bindings.Count>0&&bindings.All(b=>b.Style.IccExists),"existing native shortcuts are retained by UUID");
        string source = Path.Combine(Shortcuts.DirectoryPath, "CaptureOne Default.xml");
        string before = File.ReadAllText(source);
        var generated = Shortcuts.BuildSet(source, bindings);
        var original = Catalog.ReadXml(source);
        Check(generated.Descendants("Command").Select(n=>n.ToString()).SequenceEqual(original.Descendants("Command").Select(n=>n.ToString())), "all existing native shortcuts preserved");
        Check(generated.Descendants("SpeedEditCommand").Select(n=>n.ToString()).SequenceEqual(original.Descendants("SpeedEditCommand").Select(n=>n.ToString())), "Speed Edit preserved");
        Check(generated.Descendants("AdvancedCommand").Count()==10+bindings.Count, "ten implicit default bindings preserved alongside existing RNI bindings");
        string created = Path.Combine(root, "demo.xml");
        Shortcuts.InstallNew(source, created, bindings);
        string reason;
        Check(Shortcuts.Verify(created, bindings, out reason), "advanced commands round-trip");
        Throws(()=>Shortcuts.InstallNew(source, created, bindings), "refuse overwriting an existing key set");
        Check(before == File.ReadAllText(source), "source key set unchanged");
        string conflict = Path.Combine(root, "conflict.xml");
        var conflicted = new XDocument(original); conflicted.Root.Add(new XElement("Command",new XAttribute("CommandName","ConflictFixture"),new XAttribute("CommandShortcut",bindings[0].Shortcut))); conflicted.Save(conflict);
        Throws(()=>Shortcuts.BuildSet(conflict, bindings), "native binding conflict blocked");
        var bad = new XDocument(generated); bad.Descendants("AdvancedCommand").First(e=>(string)e.Attribute("AdvancedCommandItem")==bindings[0].Style.Uuid).SetAttributeValue("AdvancedCommandItem", Guid.NewGuid().ToString("B")); string altered=Path.Combine(root,"altered.xml");bad.Save(altered);
        Check(!Shortcuts.Verify(altered,bindings,out reason), "tampered style identity rejected");
        var preferences = new PreferencesStore(Path.Combine(root,"preferences"));
        var state = new PanelPreferences(); state.Favorites.Add(catalog.Families[0].Id); state.FavoritesInitialized=true; preferences.Save(state);
        Check(preferences.Load().Favorites.SequenceEqual(state.Favorites), "favorite persistence");
        state.TopMost=false;preferences.Save(state);Check(!preferences.Load().TopMost && File.Exists(Path.Combine(root,"preferences","favorites.json.bak")), "atomic preference update and backup");
        state.WindowPlacementSaved=true;state.WindowLeft=30;state.WindowTop=50;state.WindowWidth=490;state.WindowHeight=820;preferences.Save(state);
        Check(preferences.Load().WindowPlacementSaved&&preferences.Load().WindowWidth==490,"window placement persists with favorites");
        var shared=new PreferencesStore(Path.Combine(root,"shared-preferences"));
        Check(shared.ImportIfMissing(Path.Combine(preferences.DirectoryPath,"favorites.json")),"first run imports existing favorites without changing source");
        Check(shared.Load().Favorites.SequenceEqual(state.Favorites)&&shared.Load().WindowTop==50,"import carries favorites and placement");
        state.TopMost=true;preferences.Save(state);
        Check(!shared.ImportIfMissing(Path.Combine(preferences.DirectoryPath,"favorites.json"))&&!shared.Load().TopMost,"future releases do not overwrite shared preferences");
        Check(SelectionSummary.IsSingle("1/1 (滤镜)"),"single filtered result accepted");
        Check(SelectionSummary.IsSingle("1/8898"),"one selected among many accepted");
        Check(SelectionSummary.IsSingle("1/8,898 (滤镜)"),"thousands separator in browser total supported");
        Check(SelectionSummary.IsSingle(" 1 / 8 898 （筛选）"),"spaced summary and full-width hint supported");
        Check(!SelectionSummary.IsSingle("2/8898"),"multiple selection summary blocked");
        Check(!SelectionSummary.IsSingle("0/8898"),"empty selection summary blocked");
        Check(!SelectionSummary.IsSingle("1/0"),"invalid zero total blocked");
        Check(!SelectionSummary.IsSingle("1/1 /2")&&!SelectionSummary.IsSingle(null),"ambiguous or missing selection summary blocked");
        Check(SafetyPolicy.IsAllowedCatalog(SafetyPolicy.MainCatalogPath)&&SafetyPolicy.IsAllowedCatalog(SafetyPolicy.DemoCatalogPath),"working catalog and sandbox can be explicitly connected");
        Check(!SafetyPolicy.IsAllowedCatalog(@"C:\Other\Photography-Master.cocatalogdb")&&!SafetyPolicy.IsAllowedCatalog(null),"old or arbitrary catalogs remain outside supported scope");
        var target = new TargetSnapshot {Handle=new IntPtr(1),Title="Photography-Master",VariantId=9001,SelectedCount=1,Enabled=true};
        Check(SafetyPolicy.Check(target,9001,"Photography-Master")==null, "one armed target allowed");
        Check(SafetyPolicy.Check(target,9002,"Photography-Master")!=null, "original or other variant blocked");
        target.SelectedCount=2;Check(SafetyPolicy.Check(target,9001,"Photography-Master")!=null, "multiple selection blocked");target.SelectedCount=1;
        target.Modal=true;Check(SafetyPolicy.Check(target,9001,"Photography-Master")!=null, "modal blocked");target.Modal=false;
        Check(SafetyPolicy.Check(target,9001,"Other catalog")!=null, "document switch blocked");
        Check(SafetyPolicy.Check(null,9001,"Photography-Master")!=null, "missing target blocked");
        var armed=new TargetSnapshot{Handle=new IntPtr(1),ProcessId=1,ProcessStartTicks=100,Title="Photography-Master",DocumentPath=@"D:\photos\Photography-Master.cocatalogdb",VariantId=9001,SelectedCount=1,Enabled=true};
        target.ProcessId=1;target.ProcessStartTicks=100;target.DocumentPath=armed.DocumentPath;target.VariantUuid="known-test-guid";armed.VariantUuid=target.VariantUuid;
        Check(SafetyPolicy.Check(target,armed)==null,"full locked identity matches");
        target.DocumentPath=@"C:\pilot\Photography-Master.cocatalogdb";Check(SafetyPolicy.Check(target,armed)!=null,"same title and local ID in other catalog blocked");target.DocumentPath=armed.DocumentPath;
        target.ProcessStartTicks=101;Check(SafetyPolicy.Check(target,armed)!=null,"restarted process blocked");target.ProcessStartTicks=100;
        target.Handle=new IntPtr(2);Check(SafetyPolicy.Check(target,armed)!=null,"other C1 window blocked");
        var nextPhoto=new TargetSnapshot{Handle=armed.Handle,ProcessId=armed.ProcessId,ProcessStartTicks=armed.ProcessStartTicks,
            Title=armed.Title,DocumentPath=armed.DocumentPath,VariantId=9002,VariantUuid="second-photo-guid",SelectedCount=1,Enabled=true};
        Check(SafetyPolicy.CheckDocument(nextPhoto,armed)==null,"catalog connection permits selecting another photo between requests");
        Check(SafetyPolicy.Check(nextPhoto,armed)!=null,"each in-flight request still rejects changing its photo");
        Check(SafetyPolicy.Check(nextPhoto,nextPhoto)==null,"a new request can lock the new current photo");
        nextPhoto.DocumentPath=@"C:\Other\Photography-Master.cocatalogdb";
        Check(SafetyPolicy.CheckDocument(nextPhoto,armed)!=null,"catalog connection rejects same-title different-path document");nextPhoto.DocumentPath=armed.DocumentPath;
        nextPhoto.ProcessStartTicks++;
        Check(SafetyPolicy.CheckDocument(nextPhoto,armed)!=null,"catalog connection must be renewed after C1 restarts");
        var gate=new AttemptGate();long permit=gate.Capture();Check(gate.IsCurrent(permit),"active request epoch accepted");gate.Cancel();Check(!gate.IsCurrent(permit),"cancelled request epoch rejected");
        var demoNames=allBindings.Select(b=>b.Style.Uuid).ToArray();
        Check(NativeStylePolicy.NeedsSend(new string[0],demoNames[0],demoNames),"empty native style stack permits first application");
        Check(!NativeStylePolicy.NeedsSend(new[]{demoNames[0]},demoNames[0],demoNames),"already-current native style is an idempotent no-op");
        Check(NativeStylePolicy.NeedsSend(new[]{demoNames[0]},demoNames[1],demoNames),"native A to B transition allowed");
        Check(NativeStylePolicy.NeedsSend(new[]{demoNames[1]},demoNames[0],demoNames),"native B back to A transition allowed");
        Throws(()=>NativeStylePolicy.NeedsSend(null,demoNames[0],demoNames),"unreadable live styles fail closed");
        Throws(()=>NativeStylePolicy.NeedsSend(new[]{demoNames[0],demoNames[1]},demoNames[0],demoNames),"multiple native styles fail closed");
        Throws(()=>NativeStylePolicy.NeedsSend(new[]{"Unrelated user style"},demoNames[0],demoNames),"unrelated native style preserved");
        Throws(()=>NativeStylePolicy.NeedsSend(new string[0],"Unbound requested style",demoNames),"unbound request rejected");
        var session=new FakeLiveSession{Active=new[]{demoNames[0]},Requested=demoNames[0]};
        var unchanged=StyleWorkflow.Run(session,session.Requested,demoNames).GetAwaiter().GetResult();
        Check(!unchanged.Sent&&session.Sends==0&&session.Reads==1&&session.Validations==2,"workflow same-style request reads native state and sends nothing");
        session.Requested=demoNames[1];
        var changed=StyleWorkflow.Run(session,session.Requested,demoNames).GetAwaiter().GetResult();
        Check(changed.Sent&&session.Sends==1&&changed.ConfirmedStyle==demoNames[1],"workflow switch confirms actual native result");
        session.Requested=demoNames[0];
        Check(StyleWorkflow.Run(session,session.Requested,demoNames).GetAwaiter().GetResult().Sent&&session.Sends==2,"workflow A-B-A round trip has exactly two sends");
        var cancelled=new FakeLiveSession{Current=false,Active=new string[0],Requested=demoNames[0]};
        Throws<OperationCanceledException>(()=>StyleWorkflow.Run(cancelled,cancelled.Requested,demoNames).GetAwaiter().GetResult(),"cancelled workflow fails before any observation");
        Check(cancelled.Sends==0&&cancelled.Validations==0,"pre-cancelled workflow has no side effects");
        var cancelAfterRead=new FakeLiveSession{Active=new string[0],Requested=demoNames[0],OnRead=s=>s.Current=false};
        Throws<OperationCanceledException>(()=>StyleWorkflow.Run(cancelAfterRead,cancelAfterRead.Requested,demoNames).GetAwaiter().GetResult(),"cancellation during preflight prevents sending");
        Check(cancelAfterRead.Sends==0,"no style command after cancelled read");
        var switchedTarget=new FakeLiveSession{Active=new string[0],Requested=demoNames[0],OnValidate=s=>{if(s.Validations==2)throw new InvalidOperationException("different document");}};
        Throws<InvalidOperationException>(()=>StyleWorkflow.Run(switchedTarget,switchedTarget.Requested,demoNames).GetAwaiter().GetResult(),"target is revalidated after style inspection");
        Check(switchedTarget.Sends==0,"document change between read and send blocks command");
        var sendFailed=new FakeLiveSession{Active=new string[0],Requested=demoNames[0],SendFails=true};
        Throws<InvalidOperationException>(()=>StyleWorkflow.Run(sendFailed,sendFailed.Requested,demoNames).GetAwaiter().GetResult(),"incomplete native command is not accepted as success");
        Check(sendFailed.Sends==1&&sendFailed.Reads==1,"unknown send is never automatically retried");
        var mismatch=new FakeLiveSession{Active=new[]{demoNames[1]},Requested=demoNames[0],KeepOldStyle=true};
        Throws<InvalidOperationException>(()=>StyleWorkflow.Run(mismatch,mismatch.Requested,demoNames).GetAwaiter().GetResult(),"post-send native mismatch is not reported as confirmed");
        Check(mismatch.Sends==1,"failed native confirmation does not resend toggle");
        var cancelAfterSend=new FakeLiveSession{Active=new string[0],Requested=demoNames[0],OnSend=s=>s.Current=false};
        Throws<OperationCanceledException>(()=>StyleWorkflow.Run(cancelAfterSend,cancelAfterSend.Requested,demoNames).GetAwaiter().GetResult(),"cancellation after send stops follow-up actions");
        Check(cancelAfterSend.Sends==1&&cancelAfterSend.Reads==1,"cancel after send does not inspect or retry");
        var unreadable=new FakeLiveSession{Active=null,Requested=demoNames[0]};
        Throws<InvalidOperationException>(()=>StyleWorkflow.Run(unreadable,unreadable.Requested,demoNames).GetAwaiter().GetResult(),"missing live observation stops workflow");
        Check(unreadable.Sends==0,"unknown native state never sends");
        var mixedClear=new FakeClearSession{Active=new[]{"unrelated-preset",demoNames[0],demoNames[1]}};
        var clearResult=StyleClearWorkflow.Run(mixedClear,demoNames).GetAwaiter().GetResult();
        Check(clearResult.Sent&&mixedClear.Sends==2&&mixedClear.Active.SequenceEqual(new[]{"unrelated-preset"}),"clear removes only identified RNI entries and preserves unrelated preset");
        var noRni=new FakeClearSession{Active=new[]{"unrelated-preset"}};
        Check(!StyleClearWorkflow.Run(noRni,demoNames).GetAwaiter().GetResult().Sent&&noRni.Sends==0,"clear without RNI has no native command");
        var batch=new FakeBatchSession();
        var batchResult=BatchStyleWorkflow.Run(batch).GetAwaiter().GetResult();
        Check(batchResult.Confirmed==3&&batchResult.Sent==3&&batch.Index==1&&batch.ModeRestored,"batch verifies each primary separately and restores original primary/edit mode");
        var failedBatch=new FakeBatchSession{FailOnItem=1};
        try {BatchStyleWorkflow.Run(failedBatch).GetAwaiter().GetResult();throw new Exception("expected batch failure");}
        catch(BatchStyleException e){Check(e.Result.Confirmed==1&&failedBatch.Sends==1&&!failedBatch.ModeRestored&&failedBatch.Index==1,"batch failure reports confirmed prefix and neither retries nor navigates away");}
        var repeatedBatch=new FakeBatchSession();repeatedBatch.Items[2]=repeatedBatch.Items[1];
        Throws<BatchStyleException>(()=>BatchStyleWorkflow.Run(repeatedBatch).GetAwaiter().GetResult(),"batch rejects incomplete native navigation during preflight");
        Check(repeatedBatch.Sends==0,"batch preflight failure sends no styles");
        string badPrefs=Path.Combine(root,"badprefs");Directory.CreateDirectory(badPrefs);string badFile=Path.Combine(badPrefs,"favorites.json");File.WriteAllText(badFile,"{ corrupt original");var badStore=new PreferencesStore(badPrefs);string badOriginal=File.ReadAllText(badFile);Throws(()=>badStore.Load(),"corrupt favorites rejected");Throws(()=>badStore.Save(new PanelPreferences()),"save disabled after failed load");Check(badOriginal==File.ReadAllText(badFile),"corrupt original preserved");
        string external=Path.Combine(root,"xxe.xml");File.WriteAllText(external,"<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///C:/Windows/win.ini'>]><x>&y;</x>");Throws(()=>Catalog.ReadXml(external),"XML external entity rejected");
        string fixtureFile=args.Length>1?args[1]:Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","test-fixtures.local.json"));
        if(File.Exists(fixtureFile)) {
            var fixture=new JavaScriptSerializer().Deserialize<CatalogFixture>(File.ReadAllText(fixtureFile));
            var identity=CatalogReader.Resolve(fixture.Database,fixture.MultiVariantFile,fixture.MultiVariantIndex);Check(identity.Id==fixture.MultiVariantId&&identity.Uuid==fixture.MultiVariantUuid,"Windows read-only SQLite uniquely resolves test variant");
            Throws(()=>CatalogReader.Resolve(fixture.Database,"this-file-does-not-exist.NEF",0),"missing caption match rejected");
            Throws(()=>CatalogReader.ResolveSingle(fixture.Database,fixture.MultiVariantFile),"missing UI variant ordinal cannot fall back to original");
            Check(CatalogReader.ResolveSingle(fixture.Database,fixture.UniqueFile).Id==fixture.UniqueId,"single-file single-variant identity resolved");
        }
        Console.WriteLine("PASS " + count + " checks; styles="+catalog.FileCount+" families="+catalog.Families.Count+" warnings="+catalog.Warnings.Count);
        return 0;
    }
}
