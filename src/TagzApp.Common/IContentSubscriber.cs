﻿using TagzApp.Common.Models;

namespace TagzApp.Common;

public interface IContentSubscriber
{

	void SubscribeToContent(Hashtag tag, Action<Content> onNewContent);

}