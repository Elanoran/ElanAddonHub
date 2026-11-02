using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Models3d;

namespace ElansAddonHub
{
    // 3.6: Atlas 3D models, steps 3 + 4 - the "Model" card of an NPC page: the creature's own model, turning slowly and playing its idle (ModelView).
    //   where   wide: the top of the rail (above the map thumbnail and the facts); narrow: in the Overview right after the tooltip card. ONE card per page,
    //           moved between the two like the facts card, so there is one viewer per page; the web view is made only once the card is visible
    //   when    the npc has a display id (the client's creature cache, else the Classic baseline) - none known = no card; a character model (humanoid,
    //           CreatureDisplayInfo.ExtendedDisplayInfoID != 0) = no card (not shown yet; the card goes again if that is only known once the tables are read)
    //   states  "Loading model..." while the model tables are read (started here, the first time a viewer is needed) and the model is built; the model; a
    //           short, calm line when it cannot be shown (game data off or unavailable, the WoW folder not set, no model in this build, no WebView2)
    //   looks   (step 5) a creature with two or more looks (ModelData.LooksOf: displays that resolve to a model that can be shown, chance > 0) gets
    //           "Look" and a Segment 1 | 2 | 3 in the card header, each with its chance as a tooltip; the highest chance is shown first. Choosing another
    //           builds that display (CreatureModels.EnsureAsync) and hands it to the same viewer ("load": no new web view, the view keeps its turn and
    //           zoom, no "Loading model..." line over the look before). The card remembers nothing between pages.
    public partial class AtlasPage
    {
        public const double ModelCardHeight = 260, ModelViewHeight = 200;
        public const string ModelCardTag = "model-card";

        Border modelCard;
        ModelView modelView;
        TextBlock modelStatus, modelHint;
        DockPanel modelHead;
        int modelGen, modelEntry;
        List<NpcLook> modelLooks = new List<NpcLook>();
        int modelLook = -1;                 // 3.6 step 5: the look shown (index into modelLooks)
        bool modelSwitching;                // a look after the first is being loaded: no status line over the one before
        readonly List<RadioButton> lookButtons = new List<RadioButton>();
        int overviewTip;                 // where the model card goes in the Overview (after the tooltip card) when the rail is off

        // ---- for the tests
        public FrameworkElement ModelCardForTest => modelCard;
        public ModelView ModelViewForTest => modelView;
        public string ModelStatusForTest => modelStatus != null && modelStatus.Visibility == Visibility.Visible ? modelStatus.Text : "";
        public bool ModelCardInRailForTest => modelCard != null && railStack != null && railStack.Children.IndexOf(modelCard) == 0;
        public int ModelCardOverviewIndexForTest => modelCard != null && tabContent.TryGetValue(AtlasTabs.Overview, out var ov) && ov is StackPanel sp ? sp.Children.IndexOf(modelCard) : -1;
        public int ModelEntryForTest => modelEntry;        public Task ModelResolveForTest { get; private set; } = Task.CompletedTask;
        public Task ModelSwitchForTest { get; private set; } = Task.CompletedTask;
        public int LookCountForTest => lookButtons.Count;
        public int LookIndexForTest => modelLook;
        public bool LookSwitchShownForTest => lookButtons.Count > 0 && lookButtons[0].IsVisible;
        public IReadOnlyList<NpcLook> LooksForTest => modelLooks;
        public string[] LookTipsForTest => lookButtons.Select(b => b.ToolTip as string ?? "").ToArray();
        public string[] LookLabelsForTest => lookButtons.Select(b => b.Content as string ?? "").ToArray();
        public int LookCheckedForTest => lookButtons.FindIndex(b => b.IsChecked == true);
        // as a click on look i (the RadioButton's own Checked path)
        public void PickLookForTest(int i) { if (i >= 0 && i < lookButtons.Count) lookButtons[i].IsChecked = true; }

        public static int NpcEntryOf(AtlasPageModel m)
        {
            if (m == null || m.NotFound || m.Kind != "npc" || string.IsNullOrEmpty(m.Id) || !m.Id.StartsWith("npc:", StringComparison.Ordinal)) return 0;
            var s = m.Id.Substring(4); int q = s.IndexOf('?'); if (q >= 0) s = s.Substring(0, q);
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

        // the card, or null (not an npc, no display id known, a character model)
        Border ModelCardFor(AtlasPageModel m)
        {
            int entry = NpcEntryOf(m);
            if (entry <= 0) return null;
            if (ModelData.DisplaysOf(entry).Count == 0) return null;
            if (ModelData.Ready && ModelData.ForNpc(entry).Humanoid && ModelData.LooksOf(entry).Count == 0) return null;
            modelEntry = entry;
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = modelHead = new DockPanel { LastChildFill = true };
            modelHint = Tb("Drag to turn", "Text.Caption", Res("Brush.Text.Tertiary"));
            modelHint.VerticalAlignment = VerticalAlignment.Center; modelHint.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(modelHint, Dock.Right); head.Children.Add(modelHint);
            head.Children.Add(Tb("Model", "Text.Section"));
            g.Children.Add(head);
            var stage = new Grid { Height = ModelViewHeight, Margin = new Thickness(0, 12, 0, 0) };
            stage.SetResourceReference(Panel.BackgroundProperty, "Brush.Surface1");
            stage.SizeChanged += (s, e) => stage.Clip = new RectangleGeometry(new Rect(0, 0, stage.ActualWidth, stage.ActualHeight), 8, 8);
            modelView = new ModelView();
            stage.Children.Add(modelView);
            modelStatus = Tb("Loading model...", "Text.Secondary");
            modelStatus.TextWrapping = TextWrapping.Wrap; modelStatus.TextAlignment = TextAlignment.Center; modelStatus.MaxWidth = 280;
            modelStatus.HorizontalAlignment = HorizontalAlignment.Center; modelStatus.VerticalAlignment = VerticalAlignment.Center;
            stage.Children.Add(modelStatus);
            Grid.SetRow(stage, 1); g.Children.Add(stage);
            var mv = modelView;
            mv.StateChanged += () => { if (mv == modelView) ModelViewState(); };
            mv.PaletteRequested += () => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && mv == modelView) _ = OpenPalette(); }));     // Ctrl+K inside the viewer
            var card = Card(g);
            card.Height = ModelCardHeight; card.Tag = ModelCardTag;
            System.Windows.Automation.AutomationProperties.SetName(card, "Model of " + (m.Title ?? "the creature"));
            return card;
        }

        void ModelSay(string text)
        {
            if (modelStatus == null) return;
            modelStatus.Text = text ?? "";
            modelStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void ModelViewState()
        {
            if (modelView == null) return;
            switch (modelView.State)
            {
                case ModelView.ViewState.Shown: ModelSay(null); if (modelHint != null) modelHint.Visibility = Visibility.Visible; break;
                case ModelView.ViewState.Failed: ModelSay(Sentence("The model cannot be shown: " + (modelView.Reason ?? "unknown reason"))); break;
                case ModelView.ViewState.NoWebView: ModelSay("The model viewer needs Microsoft Edge WebView2, which is not installed on this PC."); break;
                default: ModelSay(modelSwitching ? null : "Loading model..."); break;
            }
        }

        static string Sentence(string s) => string.IsNullOrEmpty(s) ? s : s.EndsWith(".") || s.EndsWith(")") ? s : s + ".";

        // after the page is drawn: the model tables (read here the first time), the model's file, then the viewer
        void StartModel()
        {
            if (modelCard == null) return;
            int gen = ++modelGen, entry = modelEntry;
            ModelResolveForTest = ResolveModel(entry, gen);
        }

        async Task ResolveModel(int entry, int gen)
        {
            try
            {
                if (ModelData.Status == ModelData.State.NotStarted) ModelData.Start();
                if (ModelData.Status == ModelData.State.Loading) await WaitModelData(gen);
                if (gen != modelGen || modelCard == null) return;
                switch (ModelData.Status)
                {
                    case ModelData.State.Disabled: ModelSay("Game data is switched off in Settings, so the model cannot be shown."); return;
                    case ModelData.State.Unavailable: ModelSay(Sentence("The game's model data is not available" + (string.IsNullOrEmpty(ModelData.Reason) ? "" : " (" + ModelData.Reason + ")"))); return;
                    case ModelData.State.NotStarted: ModelSay("The game's model data is not read."); return;
                }
                var nm = ModelData.ForNpc(entry);
                var looks = ModelData.LooksOf(entry);
                if (looks.Count == 0 && nm.Humanoid) { HideModelCard(); return; }          // a character model: not shown yet
                if (looks.Count == 0) { ModelSay("There is no model for this creature in this game build."); Util.Log("atlas model: npc " + entry + ": " + nm.Reason); return; }
                // 3.6 step 5: the looks; the one shown first is the highest chance (equal chances: the first)
                modelLooks = looks;
                modelLook = ModelData.DefaultLook(looks);
                if (looks.Count > 1) BuildLookSwitch();
                ModelSay("Loading model...");
                int display = looks[modelLook].DisplayId;
                var r = await CreatureModels.EnsureAsync(display);
                if (gen != modelGen || modelView == null) return;
                if (!r.Ok) { ModelSay(Sentence("The model cannot be shown: " + r.Reason)); Util.Log("atlas model: npc " + entry + " display " + display + ": " + r.Reason); return; }
                if (modelLook >= 0 && modelLooks[modelLook].DisplayId != display) return;     // another look was chosen meanwhile: SwitchLook shows it
                modelView.Show(r.Path);
                ModelViewState();
            }
            catch (Exception e) { Util.Log("atlas model: " + e); if (gen == modelGen) ModelSay("The model cannot be shown."); }
        }

        // ---- 3.6 step 5: the look switch - "Look" and a Segment 1 | 2 | 3 in the header, the chance as each one's tooltip
        static string LookTip(NpcLook l) => l.Chance > 0 ? "Seen " + l.Chance.ToString(CultureInfo.InvariantCulture) + " % of the time" : "How often it is seen is not known";

        void BuildLookSwitch()
        {
            if (modelHead == null || modelLooks.Count < 2) return;
            lookButtons.Clear();
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            string grp = "atlas-model-look-" + modelGen.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < modelLooks.Count; i++)
            {
                int li = i;
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = (i + 1).ToString(CultureInfo.InvariantCulture), GroupName = grp, IsChecked = i == modelLook, ToolTip = LookTip(modelLooks[i]), Tag = "look|" + i };
                System.Windows.Automation.AutomationProperties.SetName(rb, "Look " + (i + 1) + ", " + LookTip(modelLooks[i]));
                rb.Checked += (s, e) => { if (li != modelLook) ModelSwitchForTest = SwitchLook(li, modelGen); };
                seg.Children.Add(rb); lookButtons.Add(rb);
            }
            // the 32 high Segment sits in the header's own line (the card keeps its 260)
            var host = new Border { Style = (Style)FindResource("SegmentHost"), Child = seg, Margin = new Thickness(8, -6, 0, -6), Tag = "model-looks" };
            var label = Tb("Look", "Text.Caption", Res("Brush.Text.Tertiary"));
            label.VerticalAlignment = VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label); row.Children.Add(host);
            DockPanel.SetDock(row, Dock.Right);
            // the switch takes the hint's place (a rail card has room for one of them)
            if (modelHint != null) { modelHead.Children.Remove(modelHint); modelHint = null; }
            modelHead.Children.Insert(0, row);
        }

        async Task SwitchLook(int i, int gen)
        {
            if (i < 0 || i >= modelLooks.Count || modelView == null) return;
            modelLook = i; modelSwitching = true;
            int display = modelLooks[i].DisplayId;
            try
            {
                var r = await CreatureModels.EnsureAsync(display);
                if (gen != modelGen || modelView == null || modelLook != i) return;
                if (!r.Ok) { ModelSay(Sentence("This look cannot be shown: " + r.Reason)); Util.Log("atlas model: npc " + modelEntry + " display " + display + ": " + r.Reason); return; }
                ModelSay(null);
                modelView.Load(r.Path);
                ModelViewState();
            }
            catch (Exception e) { Util.Log("atlas model: look " + display + ": " + e); if (gen == modelGen) ModelSay("This look cannot be shown."); }
        }

        Task WaitModelData(int gen)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action h = null;
            h = () => { if (ModelData.Status != ModelData.State.Loading && ModelData.Status != ModelData.State.NotStarted) { ModelData.Changed -= h; tcs.TrySetResult(true); } };
            ModelData.Changed += h;
            h();
            return tcs.Task;          // awaited on the UI thread: it carries on there
        }

        // a character model found only once the tables were read: the card goes (and the rail, when it held nothing else)
        void HideModelCard()
        {
            if (modelCard == null) return;
            (modelCard.Parent as Panel)?.Children.Remove(modelCard);
            ReleaseModel();
            columnMode = -1;
            Layout();
        }

        void ReleaseModel()
        {
            modelGen++;
            try { modelView?.Dispose(); } catch (Exception) { }
            modelView = null; modelCard = null; modelStatus = null; modelHint = null; modelEntry = 0;
            modelHead = null; modelLooks = new List<NpcLook>(); modelLook = -1; modelSwitching = false; lookButtons.Clear();
        }
    }
}
