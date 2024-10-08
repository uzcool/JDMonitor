﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using HtmlAgilityPack;
using JDMonitor.Entity;
using Microsoft.Web.WebView2.WinForms;
using MoreLinq;
using Newtonsoft.Json;
using ScrapySharp.Extensions;
using ServiceStack.OrmLite;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;
using Timer = System.Threading.Timer;

namespace JDMonitor;

public partial class MainForm : XtraForm
{
    private readonly Timer _backupTimer;

    private readonly List<string> _userAgents = new List<string>()
    {
        "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/535.1 (KHTML, like Gecko) Chrome/14.0.835.163 Safari/535.1",
        "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:6.0) Gecko/20100101 Firefox/6.0",
        "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/534.50 (KHTML, like Gecko) Version/5.1 Safari/534.50",
        "Opera/9.80 (Windows NT 6.1; U; zh-cn) Presto/2.9.168 Version/11.50",
        // "Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; Win64; x64; Trident/5.0; .NET CLR 2.0.50727; SLCC2; .NET CLR 3.5.30729; .NET CLR 3.0.30729; Media Center PC 6.0; InfoPath.3; .NET4.0C; Tablet PC 2.0; .NET4.0E)",
        // "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 6.1; WOW64; Trident/4.0; SLCC2; .NET CLR 2.0.50727; .NET CLR 3.5.30729; .NET CLR 3.0.30729; Media Center PC 6.0; .NET4.0C; InfoPath.3)",
        // "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 5.1; Trident/4.0; GTB7.0)",
        // "Mozilla/4.0 (compatible; MSIE 7.0; Windows NT 5.1)",
        // "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; SV1)",
        "Mozilla/5.0 (Windows; U; Windows NT 6.1; ) AppleWebKit/534.12 (KHTML, like Gecko) Maxthon/3.0 Safari/534.12",
        // "Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; WOW64; Trident/5.0; SLCC2; .NET CLR 2.0.50727; .NET CLR 3.5.30729; .NET CLR 3.0.30729; Media Center PC 6.0; InfoPath.3; .NET4.0C; .NET4.0E)",
        // "Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; WOW64; Trident/5.0)",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/85.0.4183.102 Safari/537.36"
    };

    private readonly Options _options;
    private readonly Timer _timer;
    private WebView2 _browser;
    private readonly object _locker = new object();

    public MainForm()
    {
        InitializeComponent();
        _options = Options.Reload();
        _timer = new Timer(DispatchTask, null, Timeout.Infinite, Timeout.Infinite);
        _backupTimer = new Timer(BackupDatabase, null, TimeSpan.FromSeconds(60), TimeSpan.FromDays(1));
    }

    private void BackupDatabase(object state)
    {
        _options.BackupDatabase();
    }

    private void DispatchTask(object state)
    {
        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);

            using var db = _options.GetDbConnection();
            var records = db.Select<Record>(r => r.IsEnable && !r.IsDeleted);
            if (!records.Any())
            {
                UpdateLog($"当前没有任何任务，{DateTime.Now}");
                return;
            }


            foreach (var record in records)
            {
                Handle(record).GetAwaiter().GetResult();
            }
        }
        finally
        {
            _timer.Change(_options.PeriodMilliseconds, _options.PeriodMilliseconds);
        }
    }

    private new async Task Handle(Record record, bool ignoreSyncPeriod = false)
    {
        try
        {
            if (record == null) return;
            if (!ignoreSyncPeriod && DateTime.Now - record.LastSyncAt < TimeSpan.FromSeconds(_options.Period))
            {
                UpdateLog($"{record} 不满足同步周期，跳过此次同步，下次同步时间={record.LastSyncAt.AddSeconds(_options.Period)}");
                return;
            }

            UpdateLog($"开始同步 {record}");
            var sw = Stopwatch.StartNew();
            using var tdb = _options.GetDbConnection();
            var content = await DownloadHtmlAsync(record.Url);
            if (string.IsNullOrEmpty(content))
            {
                sw.Stop();
                var msg = $"{record} 未能请求到网页数据，耗时={sw.Elapsed}";
                record.Remark = msg;
                tdb.Insert<Log>(new Log() { Message = msg });
                UpdateLog(msg);
                RefreshGridData();
            }
            else
            {
                UpdateLog($"{record.Title} 页面读取完成");

                string priceNode = null;
                foreach (var priceXPath in record.PriceXPathArray())
                {
                    if (string.IsNullOrEmpty(priceXPath)) continue;
                    priceNode = await InvokeJavaScriptAsync($"document.querySelector('{priceXPath}')?.innerText");
                    if (priceNode != null) break;
                }

                if (priceNode == null)
                {
                    UpdateLog($"{record.Title} 价格读取失败");
                    sw.Stop();
                    tdb.Insert<Log>(new Log()
                    {
                        Message = $"{record} 解析网页数据失败，耗时={sw.Elapsed}"
                    });
                    record.Remark = "价格读取失败";
                    SendEmail($"[价格读取失败] - {record.Title}",
                        $"链接={record.Url} {Environment.NewLine}当前价格XPath={record.PriceXPath}");
                    RefreshGridData();
                    return;
                }

                if (!float.TryParse(priceNode, out var parsedPrice))
                {
                    sw.Stop();
                    tdb.Insert<Log>(new Log()
                    {
                        Message = $"{record} 解析价格失败，耗时={sw.Elapsed}",
                        Tag = priceNode
                    });
                    record.Remark = "解析价格失败";
                    RefreshGridData();
                    return;
                }

                UpdateLog($"{record.Title} 价格={parsedPrice}");
                var price = new Price()
                {
                    RecordId = record.Id,
                    Value = parsedPrice,
                    Date = DateTime.Now.ToString("yyyy-MM-dd")
                };

                if (!string.IsNullOrEmpty(record.CouponXPath)) //采集优惠券信息
                {
                    var couponNode =
                        await InvokeJavaScriptAsync($"document.querySelector('{record.CouponXPath}')?.innerText");
                    if (couponNode != null)
                    {
                        price.Coupon = couponNode?.Trim().Replace(Environment.NewLine, "/");
                    }
                }

                if (!string.IsNullOrEmpty(record.ImageXPath)) //采集预览图
                {
                    var imgNode = await InvokeJavaScriptAsync($"document.querySelector('{record.ImageXPath}')?.src");
                    if (imgNode != null)
                    {
                        var src = imgNode;
                        if (!string.IsNullOrEmpty(src))
                        {
                            if (src.StartsWith("//")) //如果未明确指明地址协议，则使用商品页相同的协议
                            {
                                var uri = new Uri(record.Url);
                                src = uri.Scheme + ":" + src;
                            }

                            record.ImageUrl = src;
                        }
                    }
                }


                var priceStatView = tdb.Single<PriceStatView>(tdb
                    .From<Price>()
                    .Where(p => p.RecordId == record.Id)
                    .Select<Price>(x => new
                    {
                        Min = Sql.Min(x.Value),
                        Max = Sql.Max(x.Value),
                        Avg = Sql.Avg(x.Value)
                    }));
                if (price.Value < priceStatView.Min) //历史最低
                {
                    priceStatView.Min = price.Value; //更新最低价
                    SendEmail("历史最低", record, price, priceStatView);
                }

                var yesterdayDate = DateTime.Now.AddDays(-1).Date;
                var yesterdayStatView = tdb.Single<PriceStatView>(tdb
                    .From<Price>()
                    .Where(p => p.RecordId == record.Id && p.CreateAt >= yesterdayDate)
                    .Select<Price>(x => new
                    {
                        Min = Sql.Min(x.Value),
                        Max = Sql.Max(x.Value),
                        Avg = Sql.Avg(x.Value)
                    }));
                if (price.Value < yesterdayStatView.Min) //昨天到现在的价格情况
                {
                    yesterdayStatView.Min = price.Value; //更新最低价
                    SendEmail("昨天距今", record, price, priceStatView);
                }

                tdb.Insert(price);
                UpdateRecord(record, price);
            }
        }
        catch (Exception ex)
        {
            UpdateLog($"{record?.Title} 任务执行异常，详情={ex.Message}，堆栈={ex.StackTrace}");
        }
    }

    private void UpdateRecord(Record record, Price price)
    {
        for (int i = 0; i < gridView.RowCount; i++)
        {
            var row = gridView.GetRow(i) as Record;
            if (row == null || row.Id != record.Id) continue;
            row.LastSyncAt = DateTime.Now;
            row.NewestPrice = price.Value;
            row.NewestCoupon = price.Coupon;
            UpdateLog($"{record} 已在 {row.LastSyncAt} 完成同步");
        }

        RefreshGridData();
    }

    private HtmlNode QueryNode(HtmlDocument doc, string selector, bool isCssSelector = true)
    {
        if (doc == null) return null;

        if (!isCssSelector) return doc.DocumentNode.SelectSingleNode(selector);
        else return doc.DocumentNode.CssSelect(selector).FirstOrDefault();
    }

    private void RefreshGridData()
    {
        gridView.RefreshData();
        gridControl.RefreshDataSource();
    }


    private void SendEmail(string subject, string content)
    {
        var result = _options.SendEmail(subject, content, UpdateLog);
        using var db = _options.GetDbConnection();
        db.Insert<Mail>(new Mail()
        {
            Content = content,
            Subject = subject,
            Success = result,
            RecordId = -1
        });
    }

    private void SendEmail(string prefix, Record record, Price price, PriceStatView priceStatView)
    {
        var subject = $"价格下降- {prefix} - [{price.CreateAt.Date:yyyy-MM-dd}] [{record.Title}]";
        var content = $@"
商品地址={record.Url}
当前价格={price.Value}，最新优惠={record.NewestCoupon}
历史最高={priceStatView.Max},
历史最低={priceStatView.Min}
历史平均={priceStatView.Avg}
";
        var result = _options.SendEmail(subject, content, UpdateLog);
        if (!result) UpdateLog("发送邮件失败，请检查配置");
        using var db = _options.GetDbConnection();
        db.Insert<Mail>(new Mail()
        {
            Content = content,
            Subject = subject,
            Success = result,
            RecordId = record.Id
        });
    }

    private async Task<string> DownloadHtmlAsync(string url)
    {
        if (InvokeRequired)
        {
            var r = (Task<string>)Invoke(() => DownloadHtmlAsync(url));
            return await r;
        }
        else
        {
            try
            {
                if (_browser == null || _browser.CoreWebView2 == null) return string.Empty;

                UpdateLog($"开始下载 {url}");
                var index = Enumerable.Range(0, _userAgents.Count - 1).Shuffle().Shuffle().First();
                _browser.CoreWebView2.Settings.UserAgent = _userAgents[index];
                var mre = new ManualResetEvent(false);
                _browser.CoreWebView2.DOMContentLoaded += (s, e) => { mre.Set(); };
                _browser.CoreWebView2.NavigationCompleted += (s, e) => { mre.Set(); };
                _browser.CoreWebView2.Navigate(url);

                var signal = mre.WaitOne(TimeSpan.FromSeconds(3));

                //UpdateLog($"下载完成 {url}");
                var html = await _browser.CoreWebView2.ExecuteScriptAsync("document.body.outerHTML");
                var decoded = JsonConvert.DeserializeObject(html)?.ToString();
                return decoded;
            }
            catch (Exception ex)
            {
                UpdateLog($"{url} 内容下载异常，详情={ex.Message}");
                return String.Empty;
            }
        }
    }

    private async Task<string> InvokeJavaScriptAsync(string script)
    {
        if (InvokeRequired)
        {
            var r = (Task<string>)Invoke(() => InvokeJavaScriptAsync(script));
            return await r;
        }
        else
        {
            if (_browser == null || _browser.CoreWebView2 == null) return string.Empty;

            var html = await _browser.CoreWebView2.ExecuteScriptAsync(script);
            var decoded = JsonConvert.DeserializeObject(html)?.ToString();
            return decoded;
        }
    }

    private void UpdateLog(string text)
    {
        if (!InvokeRequired)
        {
            if (medLog.Lines.Length >= _options.MaxLogLine) medLog.ResetText();
            // ReSharper disable once LocalizableElement
            medLog.Text += $"[{DateTime.Now}] {text} {Environment.NewLine}";
            medLog.SelectionStart = medLog.Text.Length;
            medLog.ScrollToCaret();
        }
        else this.Invoke(new Action(() => UpdateLog(text)));
    }

    private void UpdateStatus(string text)
    {
        if (!InvokeRequired)
            toolStripStatusLabel.Text = text;
        else this.Invoke(new Action(() => UpdateStatus(text)));
    }

    private void MainForm_FormClosed(object sender, System.Windows.Forms.FormClosedEventArgs e)
    {
        _options.Save();
        SaveGridToDatabase();
        _timer.Dispose();
        _browser.Dispose();
    }

    private void RefreshGrid()
    {
        using var db = _options.GetDbConnection();
        gridControl.DataSource =
            db.Select<Record>(db.From<Record>().Where(r => r.IsDeleted == false).OrderBy(r => r.CreateAt));
        gridControl.Invalidate();
        gridView.RefreshData();
    }

    private void sbtnAdd_Click(object sender, System.EventArgs e)
    {
        var addForm = new AddForm(_browser);
        if (addForm.ShowDialog(this) == DialogResult.OK)
        {
            var record = addForm.GetRecord();
            if (record == null) return;
            using var db = _options.GetDbConnection();
            if (db.Exists<Record>(r =>
                    r.IsDeleted == false && r.Url.Equals(record.Url, StringComparison.OrdinalIgnoreCase)))
            {
                XtraMessageBox.Show(this, $"数据库已存在地址为{record.Url}的记录，请勿重复添加");
                return;
            }
            else
            {
                var id = db.Insert(record, true);
                record.Id = (int)id;
                RefreshGrid();
                SynchronizationContext.Current.Post(async (d) => { await Handle(record); }, null); //新增后立即同步一次
            }
        }
    }

    private async void MainForm_Load(object sender, EventArgs e)
    {
        UpdateLog("配置项" + Environment.NewLine + _options.ToJson());
        UpdateLog(Environment.NewLine);

        InitializeEnvironment();
        InitializeDatabase();
        RefreshGrid();
        _timer.Change(0, Timeout.Infinite);
    }

    private async void InitializeEnvironment()
    {
        UpdateLog($"正在检测浏览器环境");
        try
        {
            _browser = new WebView2();
            await _browser.EnsureCoreWebView2Async();

            UpdateLog(
                $"环境初始化完成,ProductVersion={_browser.ProductVersion}");
        }
        catch (Exception ex)
        {
            XtraMessageBox.Show(this, $"初始化环境出错，{ex.Message},内部错误={ex.InnerException?.Message}");
            Environment.Exit(-1);
        }
    }


    private void InitializeDatabase()
    {
        using var db = _options.GetDbConnection();
        db.CreateTableIfNotExists<Record>();
        db.CreateTableIfNotExists<Price>();
        db.CreateTableIfNotExists<Log>();
        db.CreateTableIfNotExists<Mail>();

        //update 2020年9月12日
        db.CreateColumnIfNotExists<Price>(p => p.Date);
        db.CreateColumnIfNotExists<Record>(r => r.ImageXPath);
        db.CreateColumnIfNotExists<Record>(r => r.ImageUrl);

        db.CreateColumnIfNotExists<Record>(i => i.IsDeleted);
        db.CreateColumnIfNotExists<Price>(i => i.IsDeleted);
        db.CreateColumnIfNotExists<Log>(i => i.IsDeleted);

        db.CreateColumnIfNotExists<Record>(i => i.Remark);
    }

    private void sbtnSave_Click(object sender, EventArgs e)
    {
        SaveGridToDatabase();
        UpdateStatus("修改已存储，将在下次同步时自动生效");
    }

    private void SaveGridToDatabase()
    {
        using var db = _options.GetDbConnection();
        for (int i = 0; i < gridView.RowCount; i++)
        {
            var row = gridView.GetRow(i) as Record;
            if (row == null) continue;
            db.Update(row, r => r.Id == row.Id);
        }
    }

    private void sbtnChart_Click(object sender, EventArgs e)
    {
        var row = gridView.GetFocusedRow() as Record;
        if (row == null) return;
        new PriceTrendForm(_options, row.Id)
        {
            StartPosition = FormStartPosition.CenterParent
        }.ShowDialog(this);
    }

    private void sbtnGoto_Click(object sender, EventArgs e)
    {
        var row = gridView.GetFocusedRow() as Record;
        if (row == null) return;
        Process.Start(row.Url);
    }

    private void sbtnSyncNow_Click(object sender, EventArgs e)
    {
        var row = gridView.GetFocusedRow() as Record;
        if (row == null) return;
        Task.Run(async () => { await Handle(row, true); });
    }

    private void sbtnClearLog_Click(object sender, EventArgs e)
    {
        medLog.ResetText();
    }

    private void sbtnShinkDatabase_Click(object sender, EventArgs e)
    {
        _options.SinkDatabase();
        UpdateStatus("压缩数据库完成");
    }

    private void sbtnDelete_Click(object sender, EventArgs e)
    {
        if (XtraMessageBox.Show(this, "确认删除？", "警告") == DialogResult.OK)
        {
            var row = gridView.GetFocusedRow() as Record;
            if (row == null) return;
            row.IsDeleted = true;
            using var db = _options.GetDbConnection();
            db.Update(row, r => r.Id == row.Id);
            RefreshGrid();
        }
    }

    private void sbtnRefresh_Click(object sender, EventArgs e)
    {
        if (XtraMessageBox.Show(this, "确认重新从数据库加载数据？", "警告") == DialogResult.OK)
        {
            RefreshGrid();
        }
    }
}