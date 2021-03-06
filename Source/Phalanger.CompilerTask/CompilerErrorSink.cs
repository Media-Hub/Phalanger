/*

 Copyright (c) 2011 DEVSENSE.

 The use and distribution terms for this software are contained in the file named License.txt, 
 which can be found in the root of the Phalanger distribution. By using this software 
 in any fashion, you are agreeing to be bound by the terms of this license.
 
 You must not remove this notice from this software.

*/
using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.Build.Utilities;
using PHP.Core;

namespace PHP.VisualStudio.PhalangerTasks
{
	class CompilerErrorSink : ErrorSink
	{
		private TaskLoggingHelper logger;

		/// <summary>
		/// Constructor for the error sink
		/// </summary>
		/// <param name="logger">This parameter should be the logger for the task being executed</param>
		public CompilerErrorSink(TaskLoggingHelper/*!*/ logger)
		{
			if (logger == null)
				throw new ArgumentNullException("logger");

			this.logger = logger;
		}

		private static string ErrorIdToCode(int id)
		{
			return (id >= 0) ? String.Format("PHP{0:d4}", id) : "";
		}

		/// <summary>
		/// Log Errors/Warnings/Messages when the compiler reports them.
		/// </summary>
		protected override bool Add(int id, string/*!*/ message, ErrorSeverity severity, int group, string fullPath, ErrorPosition pos)
		{
			string code = ErrorIdToCode(id);
			switch (severity.Value)
			{
                case ErrorSeverity.Values.FatalError:
				case ErrorSeverity.Values.Error:
                case ErrorSeverity.Values.WarningAsError:
                    logger.LogError(severity.Value.ToString(), code, "", fullPath, pos.FirstLine, pos.FirstColumn, pos.LastLine, pos.LastColumn, message);
					break;

				case ErrorSeverity.Values.Warning:
                    logger.LogWarning(severity.Value.ToString(), code, "", fullPath, pos.FirstLine, pos.FirstColumn, pos.LastLine, pos.LastColumn, message);
					break;
			}
			return true;
		}
	}
}