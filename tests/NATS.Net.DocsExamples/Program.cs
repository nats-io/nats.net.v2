﻿await new NATS.Net.DocsExamples.IntroPage().Run();
await new NATS.Net.DocsExamples.Core.IntroPage().Run();
await new NATS.Net.DocsExamples.Core.PubSubPage().Run();
await new NATS.Net.DocsExamples.Core.QueuePage().Run();
await new NATS.Net.DocsExamples.Core.ReqRepPage().Run();
await new NATS.Net.DocsExamples.JetStream.ManagingPage().Run();

Console.WriteLine("Bye");
