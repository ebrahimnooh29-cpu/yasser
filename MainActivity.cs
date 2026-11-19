using Android.App;
using System.IO;
using System.Linq;
using Android.Content;
using Android.Database.Sqlite;
using Android.OS;
using Android.Service.Notification;
using Android.Views;
using Android.Widget;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HalaqaAttendance;

// ==========================================
// 1. قاعدة البيانات المحلية المرنة (SQLite)
// ==========================================
public class AppDatabase : SQLiteOpenHelper
{
    private const string DbName = "HalaqaDB.db";
    private const int DbVersion = 2;

    public AppDatabase(Context context) : base(context, DbName, null, DbVersion) { }

    public override void OnCreate(SQLiteDatabase? db)
    {
        if (db == null) return;

        db.ExecSQL(@"CREATE TABLE Students (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Phone TEXT UNIQUE,
                        Name TEXT)");

        db.ExecSQL(@"CREATE TABLE DynamicFields (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        FieldKey TEXT UNIQUE)");

        db.ExecSQL(@"CREATE TABLE Attendance (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        StudentPhone TEXT,
                        StudentName TEXT,
                        Status TEXT,
                        RecordDate TEXT,
                        RecordTime TEXT,
                        FieldsJson TEXT,
                        UNIQUE(StudentPhone, RecordDate))");

        string[] defaultFields = {
            "الاسم", "اليوم", "التاريخ", "التلقين", "التسميع",
            "المراجعة الفردية", "المراجعة الجماعية", "التجويد", "شيخ الحلقة", "السبب"
        };

        foreach (var field in defaultFields)
        {
            var values = new ContentValues();
            values.Put("FieldKey", field);
            db.Insert("DynamicFields", null, values);
        }
    }

    public override void OnUpgrade(SQLiteDatabase? db, int oldVersion, int newVersion)
    {
        if (db == null) return;
        db.ExecSQL("DROP TABLE IF EXISTS Attendance");
        db.ExecSQL("DROP TABLE IF EXISTS DynamicFields");
        db.ExecSQL("DROP TABLE IF EXISTS Students");
        OnCreate(db);
    }

    // --- عمليات إضافة وتعديل وحذف الطلاب ---
    public bool AddStudent(string phone, string name)
    {
        string cleanPhone = CleanPhoneNumber(phone);
        if (string.IsNullOrWhiteSpace(cleanPhone) || string.IsNullOrWhiteSpace(name)) return false;

        using var db = WritableDatabase;
        var values = new ContentValues();
        values.Put("Phone", cleanPhone);
        values.Put("Name", name.Trim());
        long res = db!.InsertWithOnConflict("Students", null, values, Conflict.Replace);
        return res != -1;
    }

    public void UpdateStudent(string oldPhone, string newPhone, string newName)
    {
        string cleanNewPhone = CleanPhoneNumber(newPhone);
        if (string.IsNullOrWhiteSpace(cleanNewPhone) || string.IsNullOrWhiteSpace(newName)) return;

        using var db = WritableDatabase;
        var values = new ContentValues();
        values.Put("Phone", cleanNewPhone);
        values.Put("Name", newName.Trim());
        db!.Update("Students", values, "Phone = ?", new[] { oldPhone });

        // تحديث سجلات الحضور المرتبطة بالطالب أيضاً
        var attValues = new ContentValues();
        attValues.Put("StudentPhone", cleanNewPhone);
        attValues.Put("StudentName", newName.Trim());
        db.Update("Attendance", attValues, "StudentPhone = ?", new[] { oldPhone });
    }

    public void DeleteStudent(string phone)
    {
        using var db = WritableDatabase;
        db!.Delete("Students", "Phone = ?", new[] { phone });
        db.Delete("Attendance", "StudentPhone = ?", new[] { phone });
    }

    // --- عمليات إضافة وتعديل وحذف الأقسام ---
    public bool AddDynamicField(string fieldKey)
    {
        string cleanKey = fieldKey.Replace(":", "").Replace("*", "").Replace("_", "").Trim();
        if (string.IsNullOrWhiteSpace(cleanKey)) return false;

        using var db = WritableDatabase;
        var values = new ContentValues();
        values.Put("FieldKey", cleanKey);
        long res = db!.InsertWithOnConflict("DynamicFields", null, values, Conflict.Ignore);
        return res != -1;
    }

    public void UpdateDynamicField(string oldKey, string newKey)
    {
        string cleanNew = newKey.Replace(":", "").Replace("*", "").Replace("_", "").Trim();
        if (string.IsNullOrWhiteSpace(cleanNew)) return;

        using var db = WritableDatabase;
        var values = new ContentValues();
        values.Put("FieldKey", cleanNew);
        db!.Update("DynamicFields", values, "FieldKey = ?", new[] { oldKey });
    }

    public void DeleteDynamicField(string fieldKey)
    {
        using var db = WritableDatabase;
        db!.Delete("DynamicFields", "FieldKey = ?", new[] { fieldKey });
    }

    public List<string> GetDynamicFields()
    {
        var list = new List<string>();
        using var db = ReadableDatabase;
        using var cursor = db!.RawQuery("SELECT FieldKey FROM DynamicFields ORDER BY Id ASC", null);
        while (cursor != null && cursor.MoveToNext())
        {
            list.Add(cursor.GetString(0)!);
        }
        return list;
    }

    public List<(string Phone, string Name)> GetAllStudents()
    {
        var list = new List<(string, string)>();
        using var db = ReadableDatabase;
        using var cursor = db!.RawQuery("SELECT Phone, Name FROM Students ORDER BY Name ASC", null);
        while (cursor != null && cursor.MoveToNext())
        {
            list.Add((cursor.GetString(0)!, cursor.GetString(1)!));
        }
        return list;
    }

    // --- تسجيل وتعديل الحضور ---
    public void RecordAttendance(string rawSender, string status, Dictionary<string, string> extractedData)
    {
        var students = GetAllStudents();
        string cleanSenderPhone = CleanPhoneNumber(rawSender);
        string cardName = extractedData.ContainsKey("الاسم") ? extractedData["الاسم"] : "";

        string matchedPhone = cleanSenderPhone;
        string matchedName = (!string.IsNullOrWhiteSpace(cardName) && cardName != "لم يُحدد") ? cardName : rawSender;

        var student = students.FirstOrDefault(s =>
            (!string.IsNullOrEmpty(cleanSenderPhone) && (cleanSenderPhone.EndsWith(s.Phone) || s.Phone.EndsWith(cleanSenderPhone))) ||
            s.Name == rawSender.Trim() ||
            (!string.IsNullOrEmpty(cardName) && cardName != "لم يُحدد" && s.Name.Contains(cardName)));

        if (!string.IsNullOrEmpty(student.Phone))
        {
            matchedPhone = student.Phone;
            matchedName = student.Name;
        }
        else if (string.IsNullOrEmpty(matchedPhone))
        {
            matchedPhone = matchedName;
        }

        string resolvedDate = CardParser.ExtractOrFallbackDate(extractedData);
        string nowTime = DateTime.Now.ToString("HH:mm");
        string json = JsonSerializer.Serialize(extractedData);

        using var db = WritableDatabase;
        var values = new ContentValues();
        values.Put("StudentPhone", matchedPhone);
        values.Put("StudentName", matchedName);
        values.Put("Status", status);
        values.Put("RecordDate", resolvedDate);
        values.Put("RecordTime", nowTime);
        values.Put("FieldsJson", json);

        db!.InsertWithOnConflict("Attendance", null, values, Conflict.Replace);
    }

    // تعديل يدوي لحالة طالب في تاريخ معين من داخل التطبيق
    public void ManualUpdateAttendance(string phone, string name, string targetDate, string newStatus, string detailKey, string detailValue)
    {
        using var db = WritableDatabase;
        if (newStatus == "غائب")
        {
            db!.Delete("Attendance", "StudentPhone = ? AND RecordDate = ?", new[] { phone, targetDate });
            return;
        }

        var existing = GetAttendanceByDate(targetDate);
        var fields = existing.ContainsKey(phone) ? existing[phone].Fields : new Dictionary<string, string>();
        fields["الاسم"] = name;
        fields["التاريخ"] = targetDate;
        if (!string.IsNullOrWhiteSpace(detailKey) && !string.IsNullOrWhiteSpace(detailValue))
        {
            fields[detailKey] = detailValue.Trim();
        }

        var values = new ContentValues();
        values.Put("StudentPhone", phone);
        values.Put("StudentName", name);
        values.Put("Status", newStatus);
        values.Put("RecordDate", targetDate);
        values.Put("RecordTime", DateTime.Now.ToString("HH:mm"));
        values.Put("FieldsJson", JsonSerializer.Serialize(fields));

        db!.InsertWithOnConflict("Attendance", null, values, Conflict.Replace);
    }

    public Dictionary<string, (string Name, string Status, string Time, Dictionary<string, string> Fields)> GetAttendanceByDate(string targetDate)
    {
        var result = new Dictionary<string, (string, string, string, Dictionary<string, string>)>();

        using var db = ReadableDatabase;
        using var cursor = db!.RawQuery(
            "SELECT StudentPhone, StudentName, Status, RecordTime, FieldsJson FROM Attendance WHERE RecordDate = ?",
            new[] { targetDate });

        while (cursor != null && cursor.MoveToNext())
        {
            string phone = cursor.GetString(0)!;
            string name = cursor.GetString(1)!;
            string status = cursor.GetString(2)!;
            string time = cursor.GetString(3)!;
            string json = cursor.GetString(4)!;

            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            result[phone] = (name, status, time, fields);
        }
        return result;
    }

    // جلب التواريخ الفعالة التي سُجل فيها حضور بين تاريخين
    public List<string> GetActiveDatesInRange(string startDate, string endDate)
    {
        var dates = new List<string>();
        using var db = ReadableDatabase;
        using var cursor = db!.RawQuery(
            "SELECT DISTINCT RecordDate FROM Attendance WHERE RecordDate >= ? AND RecordDate <= ? ORDER BY RecordDate ASC",
            new[] { startDate, endDate });

        while (cursor != null && cursor.MoveToNext())
        {
            dates.Add(cursor.GetString(0)!);
        }
        return dates;
    }

    public static string CleanPhoneNumber(string input)
    {
        return Regex.Replace(input ?? "", @"[^\d]", "");
    }
}

// ==========================================
// 2. محلل النصوص والتاريخ الذكي (Card Parser)
// ==========================================
public static class CardParser
{
    public static string? DetectCardType(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        if (message.Contains("بطاقة استئذان") || (message.Contains("السبب") && message.Contains("الاسم")))
            return "مستأذن";

        if (message.Contains("بطاقة الحضور") || (message.Contains("التسميع") && message.Contains("التجويد")))
            return "حاضر";

        return null;
    }

    public static Dictionary<string, string> ParseMessage(string message, List<string> dynamicFields)
    {
        var result = new Dictionary<string, string>();

        string cleanMsg = message.Replace("*", "").Replace("_", "").Replace("\u200E", "").Replace("\u200F", "");
        string[] lines = cleanMsg.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            foreach (var field in dynamicFields)
            {
                var match = Regex.Match(line, $@"{Regex.Escape(field)}\s*:\s*(.*)$");
                if (match.Success)
                {
                    string rawVal = match.Groups[1].Value.Trim();

                    rawVal = Regex.Replace(rawVal, @"^[\.\-\s_]+$", "");
                    if (rawVal == "السورة (من»»إلى)") rawVal = "";

                    result[field] = string.IsNullOrWhiteSpace(rawVal) ? "لم يُحدد" : rawVal;
                    break;
                }
            }
        }
        return result;
    }

    public static string ExtractOrFallbackDate(Dictionary<string, string> extractedData)
    {
        string deviceDate = DateTime.Now.ToString("yyyy-MM-dd");

        if (!extractedData.TryGetValue("التاريخ", out string? rawDate) ||
            string.IsNullOrWhiteSpace(rawDate) ||
            rawDate == "لم يُحدد")
        {
            extractedData["التاريخ"] = deviceDate;
            return deviceDate;
        }

        string normalized = rawDate
            .Replace('٠', '0').Replace('١', '1').Replace('٢', '2').Replace('٣', '3').Replace('٤', '4')
            .Replace('٥', '5').Replace('٦', '6').Replace('٧', '7').Replace('٨', '8').Replace('٩', '9')
            .Trim();

        var match = Regex.Match(normalized, @"(\d{1,4}[\-/\.\\]\d{1,2}[\-/\.\\]\d{1,4})");
        if (match.Success)
        {
            normalized = match.Groups[1].Value.Replace('.', '/').Replace('-', '/').Replace('\\', '/');
        }

        string[] supportedFormats = {
            "d/M/yyyy", "dd/MM/yyyy",
            "yyyy/M/d", "yyyy/MM/dd",
            "d/M/yy", "dd/MM/yy"
        };

        if (DateTime.TryParseExact(normalized, supportedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate) ||
            DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
        {
            string formatted = parsedDate.ToString("yyyy-MM-dd");
            extractedData["التاريخ"] = formatted;
            return formatted;
        }

        extractedData["التاريخ"] = deviceDate;
        return deviceDate;
    }
}

// ==========================================
// 3. خدمة التقاط رسائل الواتساب تلقائياً
// ==========================================
[Service(Name = "com.yasser.halaqaattendance.WhatsAppListener",
         Label = "راصد بطاقات الحضور",
         Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
         Exported = true)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public class WhatsAppListener : NotificationListenerService
{
    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        if (sbn == null) return;

        string pkg = sbn.PackageName ?? "";
        if (pkg != "com.whatsapp" && pkg != "com.whatsapp.w4b") return;

        var extras = sbn.Notification?.Extras;
        if (extras == null) return;

        string title = extras.GetCharSequence(Notification.ExtraTitle)?.ToString() ?? "";
        string text = extras.GetCharSequence(Notification.ExtraText)?.ToString() ?? "";
        string bigText = extras.GetCharSequence(Notification.ExtraBigText)?.ToString() ?? text;
        string convTitle = extras.GetCharSequence(Notification.ExtraConversationTitle)?.ToString() ?? "";

        string? cardStatus = CardParser.DetectCardType(bigText);
        if (cardStatus == null) return;

        var prefs = GetSharedPreferences("HalaqaPrefs", FileCreationMode.Private);
        string targetGroup = prefs?.GetString("TargetGroup", "")?.Trim() ?? "";

        if (!string.IsNullOrEmpty(targetGroup))
        {
            bool matchesGroup = title.Contains(targetGroup) || convTitle.Contains(targetGroup);
            if (!matchesGroup) return;
        }

        string sender = title;
        if (title.Contains(":"))
        {
            var parts = title.Split(':');
            sender = parts.Last().Trim();
        }

        using var db = new AppDatabase(this);
        var fields = db.GetDynamicFields();
        var extracted = CardParser.ParseMessage(bigText, fields);

        db.RecordAttendance(sender, cardStatus, extracted);
    }
}

// ==========================================
// 4. الواجهة الرئيسية، إدارة البيانات، والتقارير
// ==========================================
[Activity(Label = "محضر الحلقة التلقائي", MainLauncher = true)]
public class MainActivity : Activity
{
    private AppDatabase _db = null!;
    private ISharedPreferences _prefs = null!;
    private TextView _statusText = null!;
    private Button _btnPickDate = null!;
    private Button _btnRangeStart = null!;
    private Button _btnRangeEnd = null!;
    private LinearLayout _editDbContainer = null!;

    private string _selectedDate = DateTime.Now.ToString("yyyy-MM-dd");
    private string _rangeStartDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).ToString("yyyy-MM-dd");
    private string _rangeEndDate = DateTime.Now.ToString("yyyy-MM-dd");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _db = new AppDatabase(this);
        _prefs = GetSharedPreferences("HalaqaPrefs", FileCreationMode.Private)!;

        var scroll = new ScrollView(this);
        var layout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        };
        layout.SetPadding(40, 40, 40, 40);

        // 1. صلاحية الإشعارات واسم المجموعة
        var btnPermission = new Button(this) { Text = "1. منح صلاحية قراءة إشعارات الواتساب" };
        btnPermission.Click += (s, e) => StartActivity(new Intent("android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS"));
        layout.AddView(btnPermission);

        var edtGroup = new EditText(this)
        {
            Hint = "اسم مجموعة الواتساب (اتركه فارغاً لالتقاط أي مجموعة)",
            Text = _prefs.GetString("TargetGroup", "")
        };
        var btnSaveGroup = new Button(this) { Text = "حفظ اسم المجموعة" };
        btnSaveGroup.Click += (s, e) =>
        {
            _prefs.Edit()!.PutString("TargetGroup", edtGroup.Text?.Trim()).Apply();
            Toast.MakeText(this, "تم حفظ اسم المجموعة", ToastLength.Short)!.Show();
        };
        layout.AddView(edtGroup);
        layout.AddView(btnSaveGroup);

        // 2. إضافة طالب جديد
        AddSectionHeader(layout, "إضافة طالب إلى قاعدة البيانات");
        var edtPhone = new EditText(this) { Hint = "رقم هاتف الطالب (مثال: 771234567)" };
        var edtName = new EditText(this) { Hint = "اسم الطالب الثلاثي" };
        var btnAddStudent = new Button(this) { Text = "حفظ الطالب وربطه بالرقم" };
        btnAddStudent.Click += (s, e) =>
        {
            if (_db.AddStudent(edtPhone.Text ?? "", edtName.Text ?? ""))
            {
                edtPhone.Text = "";
                edtName.Text = "";
                UpdateStats();
                Toast.MakeText(this, "تمت إضافة الطالب بنجاح", ToastLength.Short)!.Show();
            }
        };
        layout.AddView(edtPhone);
        layout.AddView(edtName);
        layout.AddView(btnAddStudent);

        // 3. إضافة قسم جديد للبطاقة
        AddSectionHeader(layout, "إضافة قسم جديد للبطاقة");
        var edtNewField = new EditText(this) { Hint = "اسم القسم الجديد (مثال: الواجب أو التفسير)" };
        var btnAddField = new Button(this) { Text = "إضافة القسم لقاعدة البيانات" };
        btnAddField.Click += (s, e) =>
        {
            if (_db.AddDynamicField(edtNewField.Text ?? ""))
            {
                edtNewField.Text = "";
                UpdateStats();
                Toast.MakeText(this, "تمت إضافة القسم بنجاح", ToastLength.Short)!.Show();
            }
        };
        layout.AddView(edtNewField);
        layout.AddView(btnAddField);

        // 4. خيار تفعيل التعديل على قاعدة البيانات من الإعدادات
        AddSectionHeader(layout, "إعدادات التعديل على قاعدة البيانات");
        bool isEditModeEnabled = _prefs.GetBoolean("EnableDbEdit", false);
        var swEnableDbEdit = new Switch(this)
        {
            Text = "تفعيل وضع التعديل والحذف في قاعدة البيانات",
            Checked = isEditModeEnabled
        };

        _editDbContainer = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Visibility = isEditModeEnabled ? ViewStates.Visible : ViewStates.Gone
        };

        var btnManageStudents = new Button(this) { Text = "✏️ إدارة الطلاب (تعديل الاسم/الرقم أو حذف)" };
        btnManageStudents.Click += (s, e) => ShowManageStudentsDialog();

        var btnManageFields = new Button(this) { Text = "🗂️ إدارة أقسام البطاقة (تعديل أو حذف قسم)" };
        btnManageFields.Click += (s, e) => ShowManageFieldsDialog();

        var btnManualAttendance = new Button(this) { Text = "📝 تعديل تحضير طالب يدوياً (لليوم المحدد)" };
        btnManualAttendance.Click += (s, e) => ShowManualAttendanceDialog();

        _editDbContainer.AddView(btnManageStudents);
        _editDbContainer.AddView(btnManageFields);
        _editDbContainer.AddView(btnManualAttendance);

        swEnableDbEdit.CheckedChange += (s, e) =>
        {
            _prefs.Edit()!.PutBoolean("EnableDbEdit", e.IsChecked).Apply();
            _editDbContainer.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;
        };

        layout.AddView(swEnableDbEdit);
        layout.AddView(_editDbContainer);

        // 5. إعدادات صيغة التقارير (واتساب / إكسل)
        AddSectionHeader(layout, "إعدادات صيغة التصدير");
        var swTextReport = new Switch(this)
        {
            Text = "تفعيل الرسالة التجميعية للمجموعة (واتساب)",
            Checked = _prefs.GetBoolean("EnableText", true)
        };
        swTextReport.CheckedChange += (s, e) => _prefs.Edit()!.PutBoolean("EnableText", e.IsChecked).Apply();

        var swExcelReport = new Switch(this)
        {
            Text = "تفعيل تصدير ملف إكسل (CSV) بالتفاصيل",
            Checked = _prefs.GetBoolean("EnableExcel", true)
        };
        swExcelReport.CheckedChange += (s, e) => _prefs.Edit()!.PutBoolean("EnableExcel", e.IsChecked).Apply();

        layout.AddView(swTextReport);
        layout.AddView(swExcelReport);

        // 6. التقرير اليومي (ليوم محدد)
        AddSectionHeader(layout, "أولاً: التقرير اليومي (اليوم أو يوم سابق)");
        _btnPickDate = new Button(this) { Text = $"📅 تاريخ اليوم المحدد: {_selectedDate}" };
        _btnPickDate.Click += (s, e) => ShowDatePicker(_selectedDate, picked =>
        {
            _selectedDate = picked;
            _btnPickDate.Text = $"📅 تاريخ اليوم المحدد: {_selectedDate}";
            UpdateStats();
        });

        var btnResetToday = new Button(this) { Text = "العودة لتاريخ اليوم" };
        btnResetToday.Click += (s, e) =>
        {
            _selectedDate = DateTime.Now.ToString("yyyy-MM-dd");
            _btnPickDate.Text = $"📅 تاريخ اليوم المحدد: {_selectedDate}";
            UpdateStats();
        };

        var btnGenerateDaily = new Button(this) { Text = "تصدير ومشاركة التقرير اليومي المحدد" };
        btnGenerateDaily.Click += (s, e) => GenerateDailyOutputs(swTextReport.Checked, swExcelReport.Checked, _selectedDate);

        layout.AddView(_btnPickDate);
        layout.AddView(btnResetToday);
        layout.AddView(btnGenerateDaily);

        // 7. التقرير الشهري / المرحلي الشامل (بين تاريخين)
        AddSectionHeader(layout, "ثانياً: التقرير الشهري الشامل (بين تاريخين)");
        _btnRangeStart = new Button(this) { Text = $"من تاريخ: {_rangeStartDate}" };
        _btnRangeStart.Click += (s, e) => ShowDatePicker(_rangeStartDate, picked =>
        {
            _rangeStartDate = picked;
            _btnRangeStart.Text = $"من تاريخ: {_rangeStartDate}";
        });

        _btnRangeEnd = new Button(this) { Text = $"إلى تاريخ: {_rangeEndDate}" };
        _btnRangeEnd.Click += (s, e) => ShowDatePicker(_rangeEndDate, picked =>
        {
            _rangeEndDate = picked;
            _btnRangeEnd.Text = $"إلى تاريخ: {_rangeEndDate}";
        });

        var btnPreviewPeriod = new Button(this) { Text = "📊 استعراض إحصائيات الفترة على الشاشة" };
        btnPreviewPeriod.Click += (s, e) => PreviewPeriodStats(_rangeStartDate, _rangeEndDate);

        var btnExportPeriod = new Button(this) { Text = "📤 تصدير ومشاركة التقرير الشهري الشامل" };
        btnExportPeriod.Click += (s, e) => GeneratePeriodOutputs(swTextReport.Checked, swExcelReport.Checked, _rangeStartDate, _rangeEndDate);

        layout.AddView(_btnRangeStart);
        layout.AddView(_btnRangeEnd);
        layout.AddView(btnPreviewPeriod);
        layout.AddView(btnExportPeriod);

        // 8. شاشة الاستعراض السفلية
        _statusText = new TextView(this) { TextSize = 14 };
        _statusText.SetPadding(0, 30, 0, 30);
        layout.AddView(_statusText);

        scroll.AddView(layout);
        SetContentView(scroll);
        UpdateStats();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateStats();
    }

    // ==========================================
    // نوافذ تعديل وحذف البيانات (Database Editor)
    // ==========================================
    private void ShowManageStudentsDialog()
    {
        var students = _db.GetAllStudents();
        if (students.Count == 0)
        {
            Toast.MakeText(this, "لا يوجد طلاب مسجلون حالياً", ToastLength.Short)!.Show();
            return;
        }

        string[] items = students.Select(s => $"{s.Name} ({s.Phone})").ToArray();
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("اختر طالباً للتعديل أو الحذف");
        builder.SetItems(items, (s, args) =>
        {
            var selected = students[args.Which];
            ShowEditSingleStudentDialog(selected.Phone, selected.Name);
        });
        builder.SetNegativeButton("إغلاق", (s, args) => { });
        builder.Show();
    }

    private void ShowEditSingleStudentDialog(string oldPhone, string oldName)
    {
        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetPadding(40, 20, 40, 20);

        var edtName = new EditText(this) { Text = oldName, Hint = "اسم الطالب" };
        var edtPhone = new EditText(this) { Text = oldPhone, Hint = "رقم الهاتف" };
        container.AddView(edtName);
        container.AddView(edtPhone);

        var builder = new AlertDialog.Builder(this);
        builder.SetTitle($"تعديل بيانات: {oldName}");
        builder.SetView(container);
        builder.SetPositiveButton("حفظ التعديل", (s, args) =>
        {
            _db.UpdateStudent(oldPhone, edtPhone.Text ?? oldPhone, edtName.Text ?? oldName);
            UpdateStats();
            Toast.MakeText(this, "تم تحديث بيانات الطالب", ToastLength.Short)!.Show();
        });
        builder.SetNeutralButton("حذف الطالب نهائياً", (s, args) =>
        {
            _db.DeleteStudent(oldPhone);
            UpdateStats();
            Toast.MakeText(this, "تم حذف الطالب من قاعدة البيانات", ToastLength.Short)!.Show();
        });
        builder.SetNegativeButton("إلغاء", (s, args) => { });
        builder.Show();
    }

    private void ShowManageFieldsDialog()
    {
        var fields = _db.GetDynamicFields();
        if (fields.Count == 0) return;

        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("اختر قسماً لتعديله أو حذفه");
        builder.SetItems(fields.ToArray(), (s, args) =>
        {
            string selectedField = fields[args.Which];
            var edtField = new EditText(this) { Text = selectedField };

            var editBuilder = new AlertDialog.Builder(this);
            editBuilder.SetTitle($"تعديل قسم: {selectedField}");
            editBuilder.SetView(edtField);
            editBuilder.SetPositiveButton("حفظ الاسم الجديد", (s2, a2) =>
            {
                _db.UpdateDynamicField(selectedField, edtField.Text ?? selectedField);
                UpdateStats();
                Toast.MakeText(this, "تم تعديل القسم", ToastLength.Short)!.Show();
            });
            editBuilder.SetNeutralButton("حذف القسم", (s2, a2) =>
            {
                _db.DeleteDynamicField(selectedField);
                UpdateStats();
                Toast.MakeText(this, "تم حذف القسم", ToastLength.Short)!.Show();
            });
            editBuilder.SetNegativeButton("إلغاء", (s2, a2) => { });
            editBuilder.Show();
        });
        builder.SetNegativeButton("إغلاق", (s, args) => { });
        builder.Show();
    }

    private void ShowManualAttendanceDialog()
    {
        var students = _db.GetAllStudents();
        if (students.Count == 0)
        {
            Toast.MakeText(this, "أضف طلاباً أولاً لقاعدة البيانات", ToastLength.Short)!.Show();
            return;
        }

        var attendance = _db.GetAttendanceByDate(_selectedDate);
        string[] items = students.Select(s =>
        {
            string st = attendance.ContainsKey(s.Phone) ? attendance[s.Phone].Status : "غائب";
            return $"{s.Name} [الحالة: {st}]";
        }).ToArray();

        var builder = new AlertDialog.Builder(this);
        builder.SetTitle($"تعديل تحضير يوم ({_selectedDate})");
        builder.SetItems(items, (s, args) =>
        {
            var st = students[args.Which];
            var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
            container.SetPadding(40, 20, 40, 20);

            var edtNote = new EditText(this)
            {
                Hint = "مقدار التسميع (للحاضر) أو السبب (للمستأذن)"
            };
            container.AddView(edtNote);

            var statusBuilder = new AlertDialog.Builder(this);
            statusBuilder.SetTitle($"تحديد حالة: {st.Name}");
            statusBuilder.SetView(container);
            statusBuilder.SetPositiveButton("تسجيل حاضر", (s2, a2) =>
            {
                _db.ManualUpdateAttendance(st.Phone, st.Name, _selectedDate, "حاضر", "التسميع", edtNote.Text ?? "");
                UpdateStats();
                Toast.MakeText(this, "تم تسجيل الطالب حاضراً", ToastLength.Short)!.Show();
            });
            statusBuilder.SetNeutralButton("تسجيل مستأذن", (s2, a2) =>
            {
                _db.ManualUpdateAttendance(st.Phone, st.Name, _selectedDate, "مستأذن", "السبب", edtNote.Text ?? "");
                UpdateStats();
                Toast.MakeText(this, "تم تسجيل الطالب مستأذناً", ToastLength.Short)!.Show();
            });
            statusBuilder.SetNegativeButton("تسجيل غائب", (s2, a2) =>
            {
                _db.ManualUpdateAttendance(st.Phone, st.Name, _selectedDate, "غائب", "", "");
                UpdateStats();
                Toast.MakeText(this, "تم تسجيل الطالب غائباً", ToastLength.Short)!.Show();
            });
            statusBuilder.Show();
        });
        builder.Show();
    }

    // ==========================================
    // أدوات التقويم والاستعراض والتقارير
    // ==========================================
    private void ShowDatePicker(string initialDateStr, Action<string> onDatePicked)
    {
        if (!DateTime.TryParseExact(initialDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime current))
        {
            current = DateTime.Now;
        }

        var dialog = new DatePickerDialog(this, (sender, args) =>
        {
            string formatted = $"{args.Year:D4}-{args.Month + 1:D2}-{args.DayOfMonth:D2}";
            onDatePicked(formatted);
        }, current.Year, current.Month - 1, current.Day);

        dialog.Show();
    }

    private void AddSectionHeader(LinearLayout layout, string title)
    {
        var tv = new TextView(this) { Text = $"\n--- {title} ---", TextSize = 16 };
        layout.AddView(tv);
    }

    private void UpdateStats()
    {
        var students = _db.GetAllStudents();
        var records = _db.GetAttendanceByDate(_selectedDate);
        var fields = _db.GetDynamicFields();

        var presentList = records.Values.Where(r => r.Status == "حاضر").ToList();
        var excusedList = records.Values.Where(r => r.Status == "مستأذن").ToList();
        var absentList = students.Where(s => !records.ContainsKey(s.Phone)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"📌 إحصائيات يوم: {_selectedDate}");
        sb.AppendLine($"إجمالي الطلاب المسجلين: {students.Count}");
        sb.AppendLine($"الحاضرون: {presentList.Count} | المستأذنون: {excusedList.Count} | الغائبون: {absentList.Count}");
        sb.AppendLine($"الأقسام المفعلة: {string.Join("، ", fields)}");
        sb.AppendLine("──────────────────────");

        sb.AppendLine("✅ الحاضرون:");
        if (presentList.Count == 0) sb.AppendLine("  - لا يوجد");
        foreach (var p in presentList)
        {
            string tasmee = p.Fields.ContainsKey("التسميع") ? p.Fields["التسميع"] : "-";
            sb.AppendLine($"  • {p.Name} ({p.Time}) - التسميع: {tasmee}");
        }

        sb.AppendLine("\n🟡 المستأذنون:");
        if (excusedList.Count == 0) sb.AppendLine("  - لا يوجد");
        foreach (var ex in excusedList)
        {
            string reason = ex.Fields.ContainsKey("السبب") ? ex.Fields["السبب"] : "-";
            sb.AppendLine($"  • {ex.Name} - السبب: {reason}");
        }

        sb.AppendLine("\n❌ الغائبون:");
        if (absentList.Count == 0) sb.AppendLine("  - لا يوجد");
        foreach (var ab in absentList)
        {
            sb.AppendLine($"  • {ab.Name}");
        }

        _statusText.Text = sb.ToString();
    }

    // حساب إحصائيات الفترة بين تاريخين
    private (List<string> ActiveDates, List<(string Phone, string Name, int Present, int Excused, int Absent, int Rate)> Summary)
        CalculatePeriodReport(string startDate, string endDate)
    {
        var students = _db.GetAllStudents();
        var activeDates = _db.GetActiveDatesInRange(startDate, endDate);
        int totalDays = activeDates.Count;

        var allDaysRecords = activeDates.ToDictionary(d => d, d => _db.GetAttendanceByDate(d));
        var summary = new List<(string Phone, string Name, int Present, int Excused, int Absent, int Rate)>();

        foreach (var st in students)
        {
            int present = 0;
            int excused = 0;

            foreach (var day in activeDates)
            {
                var dayMap = allDaysRecords[day];
                if (dayMap.TryGetValue(st.Phone, out var rec))
                {
                    if (rec.Status == "حاضر") present++;
                    else if (rec.Status == "مستأذن") excused++;
                }
            }

            int absent = Math.Max(0, totalDays - (present + excused));
            int rate = totalDays > 0 ? (int)Math.Round((double)present / totalDays * 100) : 0;
            summary.Add((st.Phone, st.Name, present, excused, absent, rate));
        }

        return (activeDates, summary);
    }

    private void PreviewPeriodStats(string startDate, string endDate)
    {
        var (activeDates, summary) = CalculatePeriodReport(startDate, endDate);

        var sb = new StringBuilder();
        sb.AppendLine($"📊 التقرير الشامل من ({startDate}) إلى ({endDate})");
        sb.AppendLine($"إجمالي أيام انعقاد الحلقة المسجلة: {activeDates.Count} يوم");
        sb.AppendLine("──────────────────────");

        if (summary.Count == 0)
        {
            sb.AppendLine("لا يوجد طلاب مسجلون في قاعدة البيانات.");
        }
        else
        {
            int idx = 0;
            foreach (var item in summary)
            {
                idx++;
                sb.AppendLine($"{idx}. {item.Name}:");
                sb.AppendLine($"   حضور: {item.Present} | استئذان: {item.Excused} | غياب: {item.Absent} | النسبة: {item.Rate}%");
            }
        }

        _statusText.Text = sb.ToString();
        Toast.MakeText(this, "تم تحديث شاشة الاستعراض بالأسفل", ToastLength.Short)!.Show();
    }

    // تصدير التقرير اليومي
    private void GenerateDailyOutputs(bool enableText, bool enableExcel, string targetDate)
    {
        if (!enableText && !enableExcel)
        {
            Toast.MakeText(this, "يرجى تفعيل خيار واحد على الأقل للتقرير", ToastLength.Short)!.Show();
            return;
        }

        var students = _db.GetAllStudents();
        var attendance = _db.GetAttendanceByDate(targetDate);
        var fields = _db.GetDynamicFields();

        if (enableText)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📊 *تقرير الحضور والإنجاز ليوم ({targetDate})*");
            sb.AppendLine("•┈┈┈┈• 🔏 ♾️ 🔏 •┈┈┈┈•\n");

            sb.AppendLine("✅ *قائمة الحاضرين:*");
            int presentCount = 0;
            foreach (var rec in attendance.Values.Where(r => r.Status == "حاضر"))
            {
                presentCount++;
                string tasmee = rec.Fields.ContainsKey("التسميع") ? rec.Fields["التسميع"] : "-";
                sb.AppendLine($"{presentCount}. *{rec.Name}* ({rec.Time}) | التسميع: {tasmee}");
            }
            if (presentCount == 0) sb.AppendLine("لا يوجد حضور مسجل في هذا التاريخ.");

            sb.AppendLine("\n🟡 *قائمة المستأذنين:*");
            int excusedCount = 0;
            foreach (var rec in attendance.Values.Where(r => r.Status == "مستأذن"))
            {
                excusedCount++;
                string reason = rec.Fields.ContainsKey("السبب") ? rec.Fields["السبب"] : "بدون ذكر سبب";
                sb.AppendLine($"{excusedCount}. *{rec.Name}* | السبب: {reason}");
            }
            if (excusedCount == 0) sb.AppendLine("لا يوجد مستأذنون في هذا التاريخ.");

            sb.AppendLine("\n❌ *قائمة الغائبين:*");
            int absentCount = 0;
            foreach (var st in students)
            {
                if (!attendance.ContainsKey(st.Phone))
                {
                    absentCount++;
                    sb.AppendLine($"{absentCount}. {st.Name}");
                }
            }
            if (absentCount == 0) sb.AppendLine("لا يوجد غياب في هذا التاريخ ولله الحمد.");

            var sendIntent = new Intent(Intent.ActionSend);
            sendIntent.SetType("text/plain");
            sendIntent.PutExtra(Intent.ExtraText, sb.ToString());
            StartActivity(Intent.CreateChooser(sendIntent, $"إرسال تقرير ({targetDate}) إلى واتساب"));
        }

        if (enableExcel)
        {
            var csv = new StringBuilder();
            csv.Append('\uFEFF');

            csv.Append("التاريخ,رقم الهاتف,اسم الطالب,الحالة,وقت الإرسال");
            foreach (var f in fields) csv.Append($",{f}");
            csv.AppendLine();

            foreach (var st in students)
            {
                if (attendance.TryGetValue(st.Phone, out var rec))
                {
                    csv.Append($"\"{targetDate}\",\"{st.Phone}\",\"{st.Name}\",\"{rec.Status}\",\"{rec.Time}\"");
                    foreach (var f in fields)
                    {
                        string val = rec.Fields.ContainsKey(f) ? rec.Fields[f].Replace("\"", "'") : "";
                        csv.Append($",\"{val}\"");
                    }
                    csv.AppendLine();
                }
                else
                {
                    csv.Append($"\"{targetDate}\",\"{st.Phone}\",\"{st.Name}\",\"غائب\",\"-\"");
                    foreach (var _ in fields) csv.Append(",\"-\"");
                    csv.AppendLine();
                }
            }

            string fileName = $"Halaqa_Report_{targetDate}.csv";
            string dir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
            string filePath = Path.Combine(dir, fileName);
            File.WriteAllText(filePath, csv.ToString(), Encoding.UTF8);

            Toast.MakeText(this, $"تم حفظ ملف الإكسل في التنزيلات:\n{fileName}", ToastLength.Long)!.Show();
        }
    }

    // تصدير التقرير الشهري / المرحلي الشامل بين تاريخين
    private void GeneratePeriodOutputs(bool enableText, bool enableExcel, string startDate, string endDate)
    {
        if (!enableText && !enableExcel)
        {
            Toast.MakeText(this, "يرجى تفعيل خيار واحد على الأقل للتقرير", ToastLength.Short)!.Show();
            return;
        }

        var (activeDates, summary) = CalculatePeriodReport(startDate, endDate);
        var students = _db.GetAllStudents();
        var fields = _db.GetDynamicFields();

        if (enableText)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📈 *التقرير الإحصائي الشامل للحلقة*");
            sb.AppendLine($"📅 الفترة: من ({startDate}) إلى ({endDate})");
            sb.AppendLine($"📌 إجمالي أيام الحلقة المسجلة: *{activeDates.Count}* يوم");
            sb.AppendLine("•┈┈┈┈• 🔏 ♾️ 🔏 •┈┈┈┈•\n");

            int idx = 0;
            foreach (var item in summary)
            {
                idx++;
                sb.AppendLine($"{idx}. *{item.Name}*");
                sb.AppendLine($"   ✅ حضور: {item.Present} | 🟡 استئذان: {item.Excused} | ❌ غياب: {item.Absent} | النسبة: {item.Rate}%");
            }

            var sendIntent = new Intent(Intent.ActionSend);
            sendIntent.SetType("text/plain");
            sendIntent.PutExtra(Intent.ExtraText, sb.ToString());
            StartActivity(Intent.CreateChooser(sendIntent, "إرسال التقرير الشهري إلى واتساب"));
        }

        if (enableExcel)
        {
            var csv = new StringBuilder();
            csv.Append('\uFEFF');

            // الجزء الأول في ملف الإكسل: ملخص الإحصائيات الشاملة لكل طالب
            csv.AppendLine($"\"التقرير الإحصائي الشامل من {startDate} إلى {endDate}\"");
            csv.AppendLine("رقم الهاتف,اسم الطالب,إجمالي أيام الحلقة,أيام الحضور,أيام الاستئذان,أيام الغياب,نسبة الحضور");
            foreach (var item in summary)
            {
                csv.AppendLine($"\"{item.Phone}\",\"{item.Name}\",\"{activeDates.Count}\",\"{item.Present}\",\"{item.Excused}\",\"{item.Absent}\",\"{item.Rate}%\"");
            }

            csv.AppendLine();
            csv.AppendLine("\"--- السجل التفصيلي اليومي خلال الفترة ---\"");

            // الجزء الثاني في ملف الإكسل: التفاصيل اليومية لكل طالب
            csv.Append("التاريخ,رقم الهاتف,اسم الطالب,الحالة,وقت الإرسال");
            foreach (var f in fields) csv.Append($",{f}");
            csv.AppendLine();

            foreach (var date in activeDates)
            {
                var dayAttendance = _db.GetAttendanceByDate(date);
                foreach (var st in students)
                {
                    if (dayAttendance.TryGetValue(st.Phone, out var rec))
                    {
                        csv.Append($"\"{date}\",\"{st.Phone}\",\"{st.Name}\",\"{rec.Status}\",\"{rec.Time}\"");
                        foreach (var f in fields)
                        {
                            string val = rec.Fields.ContainsKey(f) ? rec.Fields[f].Replace("\"", "'") : "";
                            csv.Append($",\"{val}\"");
                        }
                        csv.AppendLine();
                    }
                    else
                    {
                        csv.Append($"\"{date}\",\"{st.Phone}\",\"{st.Name}\",\"غائب\",\"-\"");
                        foreach (var _ in fields) csv.Append(",\"-\"");
                        csv.AppendLine();
                    }
                }
            }

            string fileName = $"Halaqa_Monthly_{startDate}_to_{endDate}.csv";
            string dir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;
            string filePath = Path.Combine(dir, fileName);
            File.WriteAllText(filePath, csv.ToString(), Encoding.UTF8);

            Toast.MakeText(this, $"تم حفظ التقرير الشهري في التنزيلات:\n{fileName}", ToastLength.Long)!.Show();
        }
    }
}

