/***************************************************************************

Copyright (c) Microsoft Corporation. All rights reserved.
This code is licensed under the Visual Studio SDK license terms.
THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.

***************************************************************************/

// SccProviderStorage.cs : The class implements a fake source control storage for the SccProvider package
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.IO;
using HgSccHelper;

namespace Microsoft.Samples.VisualStudio.SourceControlIntegration.SccProvider
{
	// This class defines basic source control status values
	public enum SourceControlStatus
	{
		scsUncontrolled = 0,
		scsCheckedIn,
		scsCheckedOut
	};

	public class SourceControlInfo
	{
		public string File { get; set; }
		public SourceControlStatus Status { get; set; }
	};

	public class SccProviderStorage
	{
		private HgScc hgscc;
		private Dictionary<string, HgFileInfo> cache;

		public SccProviderStorage()
		{
			hgscc = new HgScc();
			cache = new Dictionary<string, HgFileInfo>();
		}

		public bool IsValid
		{
			get
			{
				return !String.IsNullOrEmpty(hgscc.WorkingDir); 
			}
		}

		public SccErrors Init(string projectFile)
		{
			var work_dir = Path.GetDirectoryName(projectFile);
			Misc.Log("SccProviderStorage: {0}", work_dir);
			return hgscc.OpenProject(work_dir, SccOpenProjectFlags.CreateIfNew);
		}

		public void Close()
		{
			hgscc = new HgScc();
			cache.Clear();
		}

		/// <summary>
		/// Adds files to source control by adding them to the list of "controlled" files in the current project
		/// and changing their attributes to reflect the "checked in" status.
		/// </summary>
		public SccErrors AddFilesToStorage(IList<string> files)
		{
			Misc.Log("AddFilesToStorage");
			var lst = new List<SccAddFile>();
			foreach (var f in files)
			{
				lst.Add(new SccAddFile{ File = f, Flags = SccAddFlags.FileTypeAuto });
				Misc.Log("adding: {0}", f);
			}

			var err = hgscc.Add(IntPtr.Zero, lst.ToArray(), "Adding files");
			if (err == SccErrors.Ok)
				UpdateCache(files);

			return err;
		}

		/// <summary>
		/// Renames a "controlled" file. If the project file is being renamed, rename the whole storage file
		/// </summary>
		public SccErrors RenameFileInStorage(string strOldName, string strNewName)
		{
			var err = hgscc.Rename(IntPtr.Zero, strOldName, strNewName);
			if (err == SccErrors.Ok)
				UpdateCache(new[]{strOldName, strNewName});

			return err;
		}

		private static SourceControlStatus FromHgStatus(HgFileStatus status)
		{
			switch (status)
			{
				case HgFileStatus.Added:
					return SourceControlStatus.scsCheckedOut;
				case HgFileStatus.Clean:
					return SourceControlStatus.scsCheckedIn;
				case HgFileStatus.Deleted:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Ignored:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Modified:
					return SourceControlStatus.scsCheckedOut;
				case HgFileStatus.NotTracked:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Removed:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Tracked:
					return SourceControlStatus.scsCheckedIn;
			}

			return SourceControlStatus.scsUncontrolled;
		}

		private static HgFileStatus ToHgStatus(SourceControlStatus status)
		{
			switch (status)
			{
				case SourceControlStatus.scsCheckedIn:
					return HgFileStatus.Tracked;
				case SourceControlStatus.scsCheckedOut:
					return HgFileStatus.Modified;
				case SourceControlStatus.scsUncontrolled:
					return HgFileStatus.NotTracked;
			}

			return HgFileStatus.NotTracked;
		}

		public SccErrors GetStatusForFiles(SourceControlInfo[] files)
		{
			if (!IsValid)
				return SccErrors.UnknownError;

			var not_in_cache = new List<string>();

			foreach (var file in files)
			{
				if (!cache.ContainsKey(file.File.ToLower()))
					not_in_cache.Add(file.File);
			}

			if (not_in_cache.Count != 0)
			{
				UpdateCache(not_in_cache);
			}

			foreach (var file in files)
			{
				HgFileInfo info;
				if (cache.TryGetValue(file.File.ToLower(), out info))
				{
					file.Status = FromHgStatus(info.Status);
				}
			}

			return SccErrors.Ok;
		}

		public SccErrors GetStatusForFiles(string[] files, SourceControlStatus[] statuses)
		{
			if (!IsValid)
				return SccErrors.UnknownError;

			var not_in_cache = new List<string>();

			foreach (var file in files)
			{
				if (!cache.ContainsKey(file.ToLower()))
					not_in_cache.Add(file);
			}

			if (not_in_cache.Count != 0)
			{
				UpdateCache(not_in_cache);
			}

			for (int i = 0; i < files.Length; ++i)
			{
				HgFileInfo info;
				if (cache.TryGetValue(files[i].ToLower(), out info))
				{
					statuses[i] = FromHgStatus(info.Status);
				}
			}

			return SccErrors.Ok;
		}

		public SccErrors CheckInFiles(string[] files)
		{
			var error = hgscc.CheckIn(IntPtr.Zero, files, "");
			if (error == SccErrors.Ok)
			{
				UpdateCache(files);
			}
			return error;
		}

		public SccErrors CheckOutFiles(string[] files)
		{
			var error = hgscc.Checkout(IntPtr.Zero, files, "");
			if (error == SccErrors.Ok)
			{
				UpdateCache(files);
			}
			return error;
		}
		
		/// <summary>
		/// Returns a source control status inferred from the file's attributes on local disk
		/// </summary>
		public SourceControlStatus GetFileStatus(string filename)
		{
			Misc.Log("GetFileStatus: {0}", filename);

			var info = new SourceControlInfo { File = filename };
			var lst = new SourceControlInfo[]{ info };

			GetStatusForFiles(lst);

			return lst[0].Status;
		}

		/// <summary>
		/// Checkin a file to store by making the file on disk read only
		/// </summary>
		public void CheckinFile(string filename)
		{
			Misc.Log("Checkin file: {0}", filename);
			var files = new string[] { filename };
			CheckInFiles(files);
		}

		public void ViewHistory(string filename)
		{
			if (!IsValid)
				return;

			hgscc.History(IntPtr.Zero, filename);
		}

		/// <summary>
		/// Checkout a file from store by making the file on disk writable
		/// </summary>
		public void CheckoutFile(string filename)
		{
			Misc.Log("Checkout file: {0}", filename);
			var files = new string[] {filename};
			CheckOutFiles(files);
		}

		private void UpdateCache(IEnumerable<string> files)
		{
			var lst = new List<HgFileInfo>();
			foreach (var f in files)
			{
				var info = new HgFileInfo {File = f, Status = HgFileStatus.NotTracked};
				lst.Add(info);
			}
			
			var info_lst = lst.ToArray();

			SccErrors error = hgscc.QueryInfo(info_lst);
			if (error == SccErrors.Ok)
			{
				foreach (var info in info_lst)
				{
					cache[info.File.ToLower()] = info;
				}
			}
		}

		public void SetCacheStatus(string file, SourceControlStatus status)
		{
			HgFileInfo info;
			if (cache.TryGetValue(file.ToLower(), out info))
				info.Status = ToHgStatus(status);
		}
	}
}
