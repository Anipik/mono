// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Reflection;

namespace System
{
	partial class AppContext
	{
		// Called by the runtime
		internal static unsafe void Setup (char** pNames, char** pValues, int count)
		{
			for (int i = 0; i < count; i++)
				s_dataStore.Add (new string ((sbyte*)pNames[i]), new string ((sbyte*)pValues[i]));
		}

		static string? GetBaseDirectoryCore ()
		{
			// Fallback path for hosts that do not set APP_CONTEXT_BASE_DIRECTORY explicitly
			var directory = Path.GetDirectoryName (Assembly.GetEntryAssembly()?.Location);
			if (directory != null && !Path.EndsInDirectorySeparator (directory))
				directory += Path.DirectorySeparatorChar;

			return directory;
		}
	}
}
