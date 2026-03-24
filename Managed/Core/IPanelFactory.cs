using System;
using System.Collections.Generic;

namespace ArisenEditorFramework.Core;

public interface IPanelFactory
{
    IEditorPanel CreatePanel(string panelId);
    IEnumerable<string> GetAvailablePanelIds();
}
