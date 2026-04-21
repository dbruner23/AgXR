using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AgXR.App.Data;
using AgXR.App.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using AgXR.App.Services;

namespace AgXR.App;

[Activity(Label = "Saved Observations")]
public class SavedTagsActivity : Activity
{
    private ListView? _listView;
    private TextView? _txtCount;
    private TextView? _txtEmpty;
    private GeoTagDatabase? _database;
    private List<GeoTag> _tags = new List<GeoTag>();

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_saved_tags);

        _listView = FindViewById<ListView>(Resource.Id.listViewTags);
        _txtCount = FindViewById<TextView>(Resource.Id.txtCount);
        _txtEmpty = FindViewById<TextView>(Resource.Id.txtEmpty);
        var btnBack = FindViewById<ImageView>(Resource.Id.btnBack);

        if (btnBack != null)
        {
            btnBack.Click += (s, e) => Finish();
        }

        _database = new GeoTagDatabase();
        await LoadDataAsync();
    }

    internal Task ReloadAsync() => LoadDataAsync();

    private async Task LoadDataAsync()
    {
        if (_database == null) return;

        _tags = await _database.GetGeoTagsAsync();
        
        RunOnUiThread(() =>
        {
            if (_tags.Count == 0)
            {
                _listView!.Visibility = ViewStates.Gone;
                _txtEmpty!.Visibility = ViewStates.Visible;
                _txtCount!.Text = "0 items";
            }
            else
            {
                _listView!.Visibility = ViewStates.Visible;
                _txtEmpty!.Visibility = ViewStates.Gone;
                _txtCount!.Text = $"{_tags.Count} items";
                
                var adapter = new GeoTagAdapter(this, _tags);
                _listView.Adapter = adapter;
            }
        });
    }

    private class GeoTagAdapter : BaseAdapter<GeoTag>
    {
        private readonly SavedTagsActivity _context;
        private readonly List<GeoTag> _items;

        public GeoTagAdapter(SavedTagsActivity context, List<GeoTag> items)
        {
            _context = context;
            _items = items;
        }

        public override GeoTag this[int position] => _items[position];

        public override int Count => _items.Count;

        public override long GetItemId(int position) => _items[position].Id;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var view = convertView ?? _context.LayoutInflater.Inflate(Resource.Layout.geotag_item, parent, false);
            var item = _items[position];

            if (view != null)
            {
                var chkTrack = view.FindViewById<CheckBox>(Resource.Id.chkTrack);
                var txtCategory = view.FindViewById<TextView>(Resource.Id.txtCategory);
                var txtDescription = view.FindViewById<TextView>(Resource.Id.txtDescription);
                var txtAction = view.FindViewById<TextView>(Resource.Id.txtAction);
                var txtLocation = view.FindViewById<TextView>(Resource.Id.txtLocation);
                var txtTimestamp = view.FindViewById<TextView>(Resource.Id.txtTimestamp);
                var imgThumbnail = view.FindViewById<ImageView>(Resource.Id.imgThumbnail);

                if (chkTrack != null)
                {
                    // Remove listener to prevent triggering during recycling/binding
                    chkTrack.CheckedChange -= null;
                    // Remove all previous event handlers is tricky in C# without keeping reference.
                    // Instead, we just set the tag and use a single handler or careful binding.
                    // Simplest for now: clear CheckedChange then re-assign or use SetOnCheckedChangeListener null first?
                    // Xamarin events are multicasting.

                    // Better approach: Set OnClickListener or use Tag
                    chkTrack.Checked = item.IsTracked;

                    // Clear previous subscriptions by creating a new anonymous delegate that we can't easily unsubscribe from?
                    // No, that causes memory leaks and multiple calls.
                    // Let's use a stored delegate or simply overwrite the standard click listener which is safer for listviews.

                    chkTrack.SetOnClickListener(new CheckBoxClickListener(item, _context));
                }

                var btnDelete = view.FindViewById<ImageView>(Resource.Id.btnDelete);
                if (btnDelete != null)
                {
                    btnDelete.SetOnClickListener(new DeleteClickListener(item, _context));
                }

                if (txtCategory != null) txtCategory.Text = item.Category.ToUpper();
                if (txtDescription != null) txtDescription.Text = item.Description;
                if (txtTimestamp != null) txtTimestamp.Text = item.Timestamp.ToLocalTime().ToString("g");
                
                if (txtAction != null)
                {
                    if (!string.IsNullOrEmpty(item.Action))
                    {
                        txtAction.Text = $"Action: {item.Action}";
                        txtAction.Visibility = ViewStates.Visible;
                    }
                    else
                    {
                        txtAction.Visibility = ViewStates.Gone;
                    }
                }

                if (txtLocation != null)
                {
                    txtLocation.Text = $"Lat: {item.Latitude:F5}, Long: {item.Longitude:F5}";
                    // Highlight if GPS is missing (0,0)
                    if (System.Math.Abs(item.Latitude) < 0.0001 && System.Math.Abs(item.Longitude) < 0.0001)
                    {
                        txtLocation.SetTextColor(Android.Graphics.Color.Red);
                        txtLocation.Text += " (No GPS)";
                    }
                    else
                    {
                        txtLocation.SetTextColor(Android.Graphics.Color.DarkGray);
                    }
                }
                
                // Load image thumbnail if exists
                if (imgThumbnail != null && !string.IsNullOrEmpty(item.ImagePath) && System.IO.File.Exists(item.ImagePath))
                {
                    try
                    {
                        // Efficient formatting for thumbnail
                        var options = new Android.Graphics.BitmapFactory.Options { InSampleSize = 4 }; 
                        var bitmap = Android.Graphics.BitmapFactory.DecodeFile(item.ImagePath, options);
                        imgThumbnail.SetImageBitmap(bitmap);
                    }
                    catch
                    {
                         // Fallback icon
                         imgThumbnail.SetImageResource(Android.Resource.Drawable.IcMenuCamera);
                    }
                }
                else if (imgThumbnail != null)
                {
                    imgThumbnail.SetImageResource(Android.Resource.Drawable.IcMenuCamera);
                }
            }

            return view!;
        }
    }

    private class CheckBoxClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly GeoTag _item;
        private readonly Context _context;

        public CheckBoxClickListener(GeoTag item, Context context)
        {
            _item = item;
            _context = context;
        }

        public void OnClick(View? v)
        {
            if (v is CheckBox chk)
            {
                _item.IsTracked = chk.Checked;
                // Update DB asynchronously
                Task.Run(async () => {
                    var db = new GeoTagDatabase();
                    await db.SaveGeoTagAsync(_item); // This acts as Update if ID exists
                });
            }
        }
    }

    private class DeleteClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly GeoTag _item;
        private readonly SavedTagsActivity _context;

        public DeleteClickListener(GeoTag item, SavedTagsActivity context)
        {
            _item = item;
            _context = context;
        }

        public void OnClick(View? v)
        {
            var summary = string.IsNullOrEmpty(_item.Description)
                ? _item.Category
                : $"{_item.Category}: {_item.Description}";

            new Android.App.AlertDialog.Builder(_context)
                .SetTitle("Delete tag?")
                ?.SetMessage(summary)
                ?.SetNegativeButton("Cancel", (s, e) => { })
                ?.SetPositiveButton("Delete", async (s, e) =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(_item.ImagePath) && System.IO.File.Exists(_item.ImagePath))
                        {
                            try { System.IO.File.Delete(_item.ImagePath); }
                            catch (Exception ex) { Android.Util.Log.Warn("AgXR", $"Failed to delete image {_item.ImagePath}: {ex.Message}"); }
                        }

                        var db = new GeoTagDatabase();
                        await db.DeleteGeoTagAsync(_item);
                        Android.Util.Log.Info("AgXR", $"Deleted tag {_item.Id} ({_item.Category})");
                        await _context.ReloadAsync();
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Error("AgXR", $"Delete failed: {ex.Message}");
                        Android.Widget.Toast.MakeText(_context, $"Delete failed: {ex.Message}", Android.Widget.ToastLength.Short)?.Show();
                    }
                })
                ?.Show();
        }
    }
}
