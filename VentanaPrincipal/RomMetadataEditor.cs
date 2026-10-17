using System.Globalization;
using System.Xml;
using System.Xml.Linq;
namespace ESDERomCleaner;
internal static class RomMetadataEditor {
static readonly (string Key,string Label,string Type)[] Fields=[
("path","Nombre del fichero","path"),("name","Nombre","text"),("sortname","Nombre para ordenar","text"),("collectionsortname","Nombre para colecciones","text"),("desc","Descripción","multi"),("rating","Valoración (0–1)","rating"),("releasedate","Fecha de lanzamiento","date"),("developer","Desarrollador","text"),("publisher","Distribuidor","text"),("genre","Categoría","genre"),("players","Jugadores","text"),("favorite","Favorito","bool"),("completed","Completado","bool"),("kidgame","Apto para niños","bool"),("hidden","Oculto","bool"),("broken","No funciona","bool"),("nogamecount","Excluir del contador","bool"),("nomultiscrape","Excluir del multi-scraper","bool"),("hidemetadata","Ocultar metadatos","bool"),("playcount","Veces jugado","integer"),("playtime","Tiempo jugado (segundos)","integer"),("controller","Controlador","text"),("altemulator","Emulador alternativo","text"),("screen","Pantalla de lanzamiento","text"),("lastplayed","Última reproducción","datetime")];
public static bool Show(IWin32Window owner,RomRow row,bool createBackup,IReadOnlyList<string>? knownGenres=null){
if(row.Status!=RomStatus.Ok||string.IsNullOrWhiteSpace(row.Gamelist)||string.IsNullOrWhiteSpace(row.GamelistPath))throw new InvalidOperationException("La entrada no es editable.");
var xs=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null};var originalXml=File.ReadAllText(row.Gamelist);var doc=LoadWrappedDocument(originalXml,xs,out var xmlDeclaration);
var gs=doc.Descendants().Where(g=>g.Name.LocalName=="game"&&PathRules.Relative(g.Elements().FirstOrDefault(e=>e.Name.LocalName=="path")?.Value)==PathRules.Relative(row.GamelistPath)).ToArray();if(gs.Length!=1)throw new InvalidOperationException($"Se esperaba una entrada única en gamelist, hay {gs.Length}.");var game=gs[0];
var known=Fields.Select(f=>f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);var fields=Fields.Concat(game.Elements().Where(e=>!known.Contains(e.Name.LocalName)&&e.Name.LocalName!="path").Select(e=>(Key:e.Name.LocalName,Label:e.Name.LocalName+" (adicional)",Type:"text"))).ToArray();
using var f=new Form{Text="Metadatos — "+row.RomName,Width=920,Height=490,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,StartPosition=FormStartPosition.CenterParent};
f.HandleCreated+=(_,_)=>DarkTitleBar.Apply(f.Handle);if(AppIcon.Value is{}appIcon)f.Icon=appIcon;
// Rejilla compacta de 4 columnas (etiqueta, campo, etiqueta, campo): los campos cortos van de dos en dos, la descripción y el nombre a todo el ancho
// y los indicadores (favorito, oculto…) juntos en una sola fila.
var grid=new TableLayoutPanel{Dock=DockStyle.Fill,AutoScroll=false,ColumnCount=4,Padding=new Padding(8,6,8,0)};
grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
var cs=new Dictionary<string,Control>();var before=new Dictionary<string,string>();
var flags=new TableLayoutPanel{AutoSize=true,Anchor=AnchorStyles.None,ColumnCount=4,RowCount=2,Margin=new Padding(0,6,0,2)};for(int c=0;c<4;c++)flags.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));flags.RowStyles.Add(new RowStyle(SizeType.AutoSize));flags.RowStyles.Add(new RowStyle(SizeType.AutoSize));int flagCount=0;
int gridRow=0;int slot=0;
int NewRow(){grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));return gridRow++;}
Label MakeLabel(string text)=>new Label{Text=text,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,6,8,0)};
foreach(var x in fields){
string val=game.Elements().FirstOrDefault(e=>e.Name.LocalName==x.Key)?.Value??"";if(x.Type=="path")val=Path.GetFileName(val.Replace('\\','/'));before[x.Key]=val;
Control ctl=x.Type switch{"genre"=>MakeGenreCombo(val,knownGenres),"bool"=>new CheckBox{Checked=val.Equals("true",StringComparison.OrdinalIgnoreCase),AutoSize=true,Text=x.Label,Margin=new Padding(14,3,14,3)},"multi"=>new TextBox{Multiline=true,Height=100,ScrollBars=ScrollBars.Vertical,Text=val},"path"=>new TextBox{ReadOnly=true,Text=val},_=>new TextBox{Text=x.Type is "date" or "datetime"?ToDisplay(val,x.Type=="datetime"):val,PlaceholderText=x.Type=="date"?"dd/MM/aaaa":x.Type=="datetime"?"dd/MM/aaaa HH:mm:ss":""}};
cs[x.Key]=ctl;
if(x.Type=="bool"){
flags.Controls.Add(ctl,flagCount%4,flagCount/4);flagCount++;continue;}
ctl.Dock=DockStyle.Fill;ctl.Margin=new Padding(0,2,12,2);
bool full=x.Type is "multi" or "path"||x.Key=="name";
if(full){slot=0;int r=NewRow();grid.Controls.Add(MakeLabel(x.Label),0,r);grid.Controls.Add(ctl,1,r);grid.SetColumnSpan(ctl,3);}
else if(slot==0){int r=NewRow();grid.Controls.Add(MakeLabel(x.Label),0,r);grid.Controls.Add(ctl,1,r);slot=1;}
else{int r=gridRow-1;grid.Controls.Add(MakeLabel(x.Label),2,r);grid.Controls.Add(ctl,3,r);slot=0;}}
// Los indicadores (casillas) van al final, justo encima de los botones.
if(flagCount>0){int fr=NewRow();grid.Controls.Add(flags,0,fr);grid.SetColumnSpan(flags,4);}
grid.RowCount=gridRow;
string ReadUiValue(string key) => cs[key] switch { CheckBox check => check.Checked ? "true" : "false", ComboBox combo => combo.Text.Trim(), _ => ((TextBox)cs[key]).Text.Trim() };
var initialUi = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
foreach (var field in fields) initialUi[field.Key] = ReadUiValue(field.Key);
bool HasUnsavedChanges() => fields.Where(field => field.Key != "path").Any(field => ReadUiValue(field.Key) != initialUi[field.Key]);
var save=new Button{Text="Guardar metadatos",AutoSize=true,MinimumSize=new Size(150,30)};var close=new Button{Text="Cerrar",AutoSize=true,MinimumSize=new Size(100,30)};var buttons=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Anchor=AnchorStyles.None};buttons.Controls.Add(save);buttons.Controls.Add(close);var bar=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=42,ColumnCount=1,RowCount=1};bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));bar.Controls.Add(buttons,0,0);f.Controls.Add(grid);f.Controls.Add(bar);
// Tamaño fijo: la ventana se ajusta al contenido (sin barra de desplazamiento). Solo si hubiera tantos campos adicionales que no
// cupieran en pantalla se activa el desplazamiento.
f.Load+=(_,_)=>{grid.PerformLayout();int content=grid.GetRowHeights().Sum()+grid.Padding.Vertical;int max=Screen.FromControl(f).WorkingArea.Height-140;grid.AutoScroll=content>max;f.ClientSize=new Size(f.ClientSize.Width,Math.Min(content,max)+bar.Height);if(owner is Control oc){var ob=oc.Bounds;var wa=Screen.FromControl(oc).WorkingArea;f.Location=new Point(Math.Clamp(ob.X+(ob.Width-f.Width)/2,wa.Left,Math.Max(wa.Left,wa.Right-f.Width)),Math.Clamp(ob.Y+(ob.Height-f.Height)/2,wa.Top,Math.Max(wa.Top,wa.Bottom-f.Height)));}};close.Click+=(_,_)=>f.Close();bool saved=false;bool closingFromPrompt=false;bool allowClose=false;
// «Guardar metadatos» solo está activo mientras haya algún cambio respecto a lo que se cargó.
void UpdateSaveState(){save.Enabled=HasUnsavedChanges();}
foreach(var control in cs.Values){if(control is CheckBox cb)cb.CheckedChanged+=(_,_)=>UpdateSaveState();else if(control is ComboBox combo)combo.TextChanged+=(_,_)=>UpdateSaveState();else if(control is TextBox tb&&!tb.ReadOnly)tb.TextChanged+=(_,_)=>UpdateSaveState();}
save.Enabled=false;
save.Click += (_, _) =>
{
    try
    {
        // Solo validar y escribir los campos que el usuario ha cambiado.
        // Los enteros vacíos que ya existían en el XML no deben bloquear, por
        // ejemplo, un cambio en "Favorito". v79 representaba esos enteros como 0.
        var changedFields = fields
            .Where(field => field.Key != "path" && ReadUiValue(field.Key) != initialUi[field.Key])
            .ToArray();

        if (changedFields.Length == 0)
        {
            saved = true;
            f.Close();
            return;
        }

        var vals = fields.ToDictionary(
            field => field.Key,
            field => ReadUiValue(field.Key),
            StringComparer.OrdinalIgnoreCase);

        foreach (var field in changedFields)
        {
            string value = vals[field.Key];
            XmlConvert.VerifyXmlChars(value);

            if (field.Type == "rating")
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    vals[field.Key] = "0.0";
                    continue;
                }

                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rating) || rating < 0 || rating > 1)
                    throw new FormatException("La valoración debe estar entre 0 y 1.");
                vals[field.Key] = rating.ToString("0.0", CultureInfo.InvariantCulture);
            }

            if (field.Type == "integer")
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    vals[field.Key] = "0";
                    continue;
                }

                if (!long.TryParse(value, out long integerValue) || integerValue < 0)
                    throw new FormatException(field.Label + " debe ser entero ≥ 0.");
                vals[field.Key] = integerValue.ToString(CultureInfo.InvariantCulture);
            }

            if (field.Type is "date" or "datetime" && value.Length > 0)
                vals[field.Key] = ToEsDate(value, field.Type == "datetime");
        }

        if (vals["path"] != before["path"])
            throw new InvalidOperationException("La ruta es de solo lectura.");

        foreach (var field in changedFields)
        {
            string value = vals[field.Key];
            if ((field.Type == "bool" && value == "false") ||
                (field.Type is "integer" or "rating" && (value == "0" || value == "0.0")) ||
                value == "")
            {
                game.Elements().FirstOrDefault(element => element.Name.LocalName == field.Key)?.Remove();
                continue;
            }

            var node = game.Elements().FirstOrDefault(element => element.Name.LocalName == field.Key);
            if (node == null) game.Add(new XElement(field.Key, value));
            else node.Value = value;
        }

        string tmp = Path.Combine(Path.GetDirectoryName(row.Gamelist)!, ".gamelist_" + Guid.NewGuid().ToString("N") + ".tmp");
        string bak = row.Gamelist + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        try
        {
            var updatedXml = SerializeWrappedDocument(doc, xmlDeclaration);
            File.WriteAllText(tmp, updatedXml, new System.Text.UTF8Encoding(false));
            _ = LoadWrappedDocument(updatedXml, xs, out _);
            if (createBackup) File.Copy(row.Gamelist, bak, false);
            File.Copy(tmp, row.Gamelist, true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        foreach (var field in fields) initialUi[field.Key] = ReadUiValue(field.Key);
        UpdateSaveState();
        saved = true;
        if (!closingFromPrompt) f.Close();
    }
    catch (Exception error)
    {
        MessageBox.Show(f, error.Message, "No se guardaron los metadatos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
};f.FormClosing += (_, e) =>
{
    if (allowClose || !HasUnsavedChanges()) return;
    var answer = MessageBox.Show(f, "Hay cambios sin guardar. ¿Desea guardarlos antes de cerrar?", "Cambios sin guardar", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
    if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
    if (answer == DialogResult.No) return;
    saved = false;
    closingFromPrompt = true;
    save.PerformClick();
    closingFromPrompt = false;
    if (!saved) { e.Cancel = true; return; }
    allowClose = true;
};
f.ShowDialog(owner);return saved;}
private static XDocument LoadWrappedDocument(string xml, XmlReaderSettings settings, out string declaration)
{
    var match = System.Text.RegularExpressions.Regex.Match(xml, @"^\s*<\?xml\b.*?\?>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    declaration = match.Success ? match.Value : "";
    string content = match.Success ? xml[match.Length..] : xml;
    string wrapped = "<__esde_document>" + content + "</__esde_document>";
    using var input = new System.IO.StringReader(wrapped);
    using var reader = XmlReader.Create(input, settings);
    return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
}
private static string SerializeWrappedDocument(XDocument document, string declaration)
{
    if (document.Root is null || document.Root.Name.LocalName != "__esde_document")
        throw new InvalidOperationException("La estructura interna de metadatos no es válida.");
    return declaration + string.Concat(document.Root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
}// Desplegable editable con las categorías que ya existen en la colección, para mantener el mismo formato en todos los juegos.
static ComboBox MakeGenreCombo(string value,IReadOnlyList<string>? known){var items=(known??Array.Empty<string>()).Where(g=>!string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g=>g,StringComparer.OrdinalIgnoreCase).ToList();if(value.Length>0&&!items.Contains(value,StringComparer.OrdinalIgnoreCase))items.Insert(0,value);var combo=new ComboBox{DropDownStyle=ComboBoxStyle.DropDown,AutoCompleteMode=AutoCompleteMode.SuggestAppend,AutoCompleteSource=AutoCompleteSource.ListItems,MaxDropDownItems=16,IntegralHeight=false};combo.Items.AddRange(items.Cast<object>().ToArray());combo.Text=value;return combo;}
static string ToDisplay(string v,bool time){if(v==""||v=="0")return "";string[] fs=["yyyyMMdd'T'HHmmss","yyyyMMdd","yyyy-MM-dd'T'HH:mm:ss","yyyy-MM-dd"];return DateTime.TryParseExact(v,fs,CultureInfo.InvariantCulture,DateTimeStyles.None,out var d)?d.ToString(time?"dd/MM/yyyy HH:mm:ss":"dd/MM/yyyy",CultureInfo.InvariantCulture):v;}
static string ToEsDate(string v,bool time){string[] fs=time?["dd/MM/yyyy HH:mm:ss","dd/MM/yyyy HH:mm","yyyy-MM-dd HH:mm:ss","yyyy-MM-dd'T'HH:mm:ss"]:["dd/MM/yyyy","yyyy-MM-dd"];if(!DateTime.TryParseExact(v,fs,CultureInfo.InvariantCulture,DateTimeStyles.None,out var d))throw new FormatException("Fecha no válida: "+v);return d.ToString("yyyyMMdd'T'HHmmss",CultureInfo.InvariantCulture);}
}