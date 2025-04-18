/*
 * Copyright (c) Facebook, Inc. and its affiliates.
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this source tree.
 */

using Microsoft.AspNetCore.Hosting;
using React.Exceptions;

namespace React.AspNet
{
    /// <summary>
    /// Handles file system functionality, such as reading files. Maps all paths from
    /// application-relative (~/...) to full paths using ASP.NET's MapPath method.
    /// </summary>
    public class AspNetFileSystem : FileSystemBase
	{
		private readonly IWebHostEnvironment _hostingEnv;

		/// <summary>
		/// Initializes a new instance of the <see cref="AspNetFileSystem"/> class.
		/// </summary>
		/// <param name="hostingEnv">The .NET Core hosting environment</param>
		public AspNetFileSystem(IWebHostEnvironment hostingEnv)
		{
			_hostingEnv = hostingEnv;
		}

		/// <summary>
		/// Converts a path from an application relative path (~/...) to a full filesystem path
		/// </summary>
		/// <param name="relativePath">App-relative path of the file</param>
		/// <returns>Full path of the file</returns>
		public override string MapPath(string relativePath)
		{
			if (_hostingEnv.WebRootPath == null)
			{
				throw new ReactException("WebRootPath was null, has the wwwroot folder been deployed along with your app?");
			}

			if (relativePath.StartsWith(_hostingEnv.WebRootPath))
			{
				return relativePath;
			}
			relativePath = relativePath.TrimStart('~').TrimStart('/').TrimStart('\\');

			return Path.GetFullPath(Path.Combine(_hostingEnv.WebRootPath, relativePath));
		}
	}
}
