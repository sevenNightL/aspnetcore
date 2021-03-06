// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Components.WebView.Document
{
    internal class MarkupNode : TestNode
    {
        public MarkupNode(string markupContent)
        {
            Content = markupContent;
        }

        public string Content { get; }
    }
}
