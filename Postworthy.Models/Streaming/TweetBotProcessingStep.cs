﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;
using Postworthy.Models.Twitter;
using Postworthy.Models.Account;
using System.Threading.Tasks;
using System.IO;
using System.Collections;
using Postworthy.Models.Repository;
using Postworthy.Models.Core;
using System.Runtime.ConstrainedExecution;

namespace Postworthy.Models.Streaming
{
    public class TweetBotProcessingStep : IProcessingStep
    {
        private const string RUNTIME_REPO_KEY = "TweetBotRuntimeSettings";
        private const int POTENTIAL_TWEET_BUFFER_MAX = 10;
        private const int POTENTIAL_TWEEP_BUFFER_MAX = 50;
        private const int MIN_TWEEP_NOTICED = 5;
        private const int TWEEP_NOTICED_AUTOMATIC = 25;
        private const int MAX_TIME_BETWEEN_TWEETS = 3;
        private int saveCount = 0;
        private List<string> NoTweetList = new List<string>();
        private string[] Messages = null;
        private bool OnlyWithMentions = false;
        private bool SimulationMode = false;
        private TextWriter log = null;
        private Tweep PrimaryTweep = new Tweep(UsersCollection.PrimaryUser(), Tweep.TweepType.None);
        private TweetBotRuntimeSettings RuntimeSettings = null;
        private Repository<TweetBotRuntimeSettings> repo = Repository<TweetBotRuntimeSettings>.Instance;

        public void Init(TextWriter log)
        {
            this.log = log;

            RuntimeSettings = (repo.Query(RUNTIME_REPO_KEY)
                ?? new List<TweetBotRuntimeSettings> { new TweetBotRuntimeSettings() }).FirstOrDefault() 
                ?? new TweetBotRuntimeSettings();

            NoTweetList.Add(UsersCollection.PrimaryUser().TwitterScreenName.ToLower());
            Messages = TweetBotSettings.Get.Messages.Count == 0 ?
                null :
                Enumerable.Range(0, TweetBotSettings.Get.Messages.Count - 1)
                    .Select(i => TweetBotSettings.Get.Messages[i].Value).ToArray();
            OnlyWithMentions = TweetBotSettings.Get.Filters["OnlyWithMentions"] != null ?
                TweetBotSettings.Get.Filters["OnlyWithMentions"].Value :
                false;

            SimulationMode = TweetBotSettings.Get.Settings["IsSimulationMode"] != null ?
                TweetBotSettings.Get.Settings["IsSimulationMode"].Value :
                false;

            if(SimulationMode)
                log.WriteLine("{0}: Running in Simulation Mode, no real world actions will be taken.",
                    DateTime.Now);

            if (Messages == null)
                log.WriteLine("{0}: 'TweetBotSettings' configuration section is missing Messages. No responses will be sent.",
                    DateTime.Now);
            else
            {
                log.WriteLine("{0}: TweetBot will respond with: {1}",
                    DateTime.Now,
                    Environment.NewLine + string.Join(Environment.NewLine, Messages));
            }
        }

        public Task<IEnumerable<Tweet>> ProcessItems(IEnumerable<Tweet> tweets)
        {
            return Task<IEnumerable<Tweet>>.Factory.StartNew(new Func<IEnumerable<Tweet>>(() =>
                {
                    RespondToTweets(tweets);

                    FindPotentialTweets(tweets);

                    FindTweepsToFollow(tweets);

                    UpdateAverageWeight(tweets);

                    SendTweets();

                    EstablishTargets();

                    DebugConsoleLog();

                    if (saveCount++ > 20)
                    {
                        SaveRuntimeSettings();
                        saveCount = 0;
                    }

                    return tweets;
                }));
        }

        public void Shutdown()
        {
            SaveRuntimeSettings();
        }

        private void SaveRuntimeSettings()
        {
            bool saved = false;
            while (!saved)
            {
                try
                {
                    repo.Save(RUNTIME_REPO_KEY, RuntimeSettings);
                    saved = true;
                }
                catch (Enyim.Caching.Memcached.MemcachedException mcex)
                {
                    RuntimeSettings.Tweeted = RuntimeSettings.Tweeted
                        .OrderByDescending(x=>x.TweetRank)
                        .Take(RuntimeSettings.Tweeted.Count() / 2)
                        .ToList();
                    saved = false;
                }
            }
        }

        private void SendTweets()
        {
            if (RuntimeSettings.TweetOrRetweet)
            {
                RuntimeSettings.TweetOrRetweet = !RuntimeSettings.TweetOrRetweet;
                if (RuntimeSettings.PotentialTweets.Count >= POTENTIAL_TWEET_BUFFER_MAX ||
                    //Because we default the LastTweetTime to the max value this will only be used after the tweet buffer initially loads up
                    DateTime.Now >= RuntimeSettings.LastTweetTime.AddHours(MAX_TIME_BETWEEN_TWEETS))
                {
                    var tweet = RuntimeSettings.PotentialTweets.First();
                    var groups = RuntimeSettings.Tweeted
                        .Union(new List<Tweet> { tweet }, Tweet.GetTweetTextComparer())
                        .GroupSimilar(0.45m, log)
                        .Select(g => new TweetGroup(g))
                        .Where(g=>g.GroupStatusIDs.Count() > 1);
                    var matches = groups.Where(x => x.GroupStatusIDs.Contains(tweet.StatusID));
                    if (matches.Count() > 0)
                    {
                        //Ignore Tweets that are very similar
                        RuntimeSettings.PotentialTweets.Remove(tweet);
                    }
                    else
                    {
                        if (SendTweet(tweet, false))
                        {
                            RuntimeSettings.Tweeted = RuntimeSettings.Tweeted.Union(new List<Tweet> { tweet }, Tweet.GetTweetTextComparer()).ToList();
                            RuntimeSettings.TweetsSentSinceLastFriendRequest++;
                            RuntimeSettings.LastTweetTime = DateTime.Now;
                            RuntimeSettings.PotentialTweets.Remove(tweet);
                            RuntimeSettings.PotentialTweets.RemoveAll(x => x.RetweetCount < GetMinRetweets());
                        }
                        else
                            RuntimeSettings.PotentialReTweets.Remove(tweet);
                    }
                }
            }
            else
            {
                RuntimeSettings.TweetOrRetweet = !RuntimeSettings.TweetOrRetweet;
                if (RuntimeSettings.PotentialReTweets.Count >= POTENTIAL_TWEET_BUFFER_MAX || 
                    //Because we default the LastTweetTime to the max value this will only be used after the tweet buffer initially loads up
                    DateTime.Now >= RuntimeSettings.LastTweetTime.AddHours(MAX_TIME_BETWEEN_TWEETS))
                {
                    var tweet = RuntimeSettings.PotentialReTweets.First();
                     var groups = RuntimeSettings.Tweeted.Union(new List<Tweet> { tweet }, Tweet.GetTweetTextComparer())
                        .GroupSimilar(0.45m, log)
                        .Select(g => new TweetGroup(g))
                        .Where(g=>g.GroupStatusIDs.Count() > 1);
                     var matches = groups.Where(x => x.GroupStatusIDs.Contains(tweet.StatusID));
                     if (matches.Count() > 0)
                     {
                         //Ignore Tweets that are very similar
                         RuntimeSettings.PotentialReTweets.Remove(tweet);
                     }
                     else
                     {
                         if (SendTweet(tweet, true))
                         {
                             RuntimeSettings.Tweeted = RuntimeSettings.Tweeted.Union(new List<Tweet> { tweet }, Tweet.GetTweetTextComparer()).ToList();
                             RuntimeSettings.TweetsSentSinceLastFriendRequest++;
                             RuntimeSettings.LastTweetTime = DateTime.Now;
                             RuntimeSettings.PotentialReTweets.Remove(tweet);
                             RuntimeSettings.PotentialReTweets.RemoveAll(x => x.RetweetCount < GetMinRetweets());
                         }
                         else
                             RuntimeSettings.PotentialReTweets.Remove(tweet);
                     }
                }
            }
        }

        private bool SendTweet(Tweet tweet, bool isRetweet)
        {
            if (!SimulationMode)
            {

                if (!isRetweet)
                {
                    tweet.PopulateExtendedData();
                    var link = tweet.Links.OrderByDescending(x => x.ShareCount).FirstOrDefault();
                    if (link != null)
                    {
                        string statusText = link.ToString() == link.Title ?
                            link.Title.Substring(0, 116) + " " + link.Uri.ToString()
                            :
                            link.Uri.ToString();
                        TwitterModel.Instance.UpdateStatus(statusText, processStatus: false);
                        return true;
                    }
                }
                else
                {
                    TwitterModel.Instance.Retweet(tweet.StatusID.ToString());
                    return true;
                }

                return false;
            }
            else 
                return true;
        }

        private void EstablishTargets()
        {
            if (RuntimeSettings.TweetsSentSinceLastFriendRequest >= 2)
            {
                RuntimeSettings.TweetsSentSinceLastFriendRequest = 0;

                var tweeps = RuntimeSettings.PotentialTweeps
                    .Where(x => x.Item1 > MIN_TWEEP_NOTICED)
                    .Where(x => x.Item2.Type == Tweep.TweepType.None);

                tweeps.ToList().ForEach(x=>{
                    var followers = x.Item2.Followers().Select(y=>y.ID);
                    var primaryFollowers = PrimaryTweep.Followers().Select(y => y.ID);

                    if (x.Item1 > TWEEP_NOTICED_AUTOMATIC || 
                        followers.Union(primaryFollowers).Count() != (followers.Count() + primaryFollowers.Count()))
                    {
                        x.Item2.Type = Tweep.TweepType.Target;

                        if (!SimulationMode)
                        {
                            var follow = TwitterModel.Instance.CreateFriendship(x.Item2);

                            if (follow.Type == Tweep.TweepType.Following)
                            {
                                PrimaryTweep.Followers(true);
                                RuntimeSettings.PotentialTweeps.Remove(x);
                            }
                        }
                    }
                    else
                        x.Item2.Type = Tweep.TweepType.Ignore;
                });
            }
        }

        private void DebugConsoleLog()
        {
            log.WriteLine("****************************");
            log.WriteLine("****************************");
            if (RuntimeSettings.Tweeted.Count() > 0)
            {
                log.WriteLine("####################");
                log.WriteLine("{0}: Past Tweets: {1}",
                    DateTime.Now,
                    Environment.NewLine + "\t" + string.Join(Environment.NewLine + "\t", RuntimeSettings.Tweeted.Select(x => (x.RetweetCount + 1) + ":" + x.TweetText)));
                log.WriteLine("####################");
            }
            if (RuntimeSettings.PotentialTweets.Count() > 0)
            {
                log.WriteLine("####################");
                log.WriteLine("{0}: Potential Tweets: {1}",
                    DateTime.Now,
                    Environment.NewLine + "\t" + string.Join(Environment.NewLine + "\t", RuntimeSettings.PotentialTweets.Select(x => (x.RetweetCount + 1) + ":" + x.TweetText)));
                log.WriteLine("####################");
            }
            if (RuntimeSettings.PotentialReTweets.Count() > 0)
            {
                log.WriteLine("####################");
                log.WriteLine("{0}: Potential Retweets: {1}",
                    DateTime.Now,
                    Environment.NewLine + "\t" + string.Join(Environment.NewLine + "\t", RuntimeSettings.PotentialReTweets.Select(x => (x.RetweetCount + 1) + ":" + x.TweetText)));
                log.WriteLine("####################");
            }
            if (RuntimeSettings.PotentialTweeps.Count() > 0)
            {
                log.WriteLine("####################");
                log.WriteLine("{0}: Potential Tweeps: {1}",
                    DateTime.Now,
                    Environment.NewLine + "\t" + string.Join(Environment.NewLine + "\t", RuntimeSettings.PotentialTweeps
                        .OrderByDescending(x=>x.Item2.User.FollowersCount.ToString().Length)
                        .ThenByDescending(x=>x.Item1)
                        .ThenBy(x=>x.Item2.ScreenName)
                        .Select(x => x.Item1.ToString().PadLeft(3,'0') + "\t" + x.Item2)));
                log.WriteLine("####################");
            }

            log.WriteLine("####################");
            log.WriteLine("{0}: Minimum Weight: {1:F5}",
                DateTime.Now,
                GetMinWeight());

            log.WriteLine("{0}: Minimum Retweets: {1:F2}",
                DateTime.Now,
                GetMinRetweets());

            log.WriteLine("{0}: Running {1}",
                DateTime.Now,
                SimulationMode ? "**In Simulation Mode**" : "**In the Wild**");
            log.WriteLine("####################");

            log.WriteLine("****************************");
            log.WriteLine("****************************");
        }

        private void UpdateAverageWeight(IEnumerable<Tweet> tweets)
        {
            var minClout = GetMinClout();
            var minWeight = GetMinWeight();
            var friendsAndFollows = PrimaryTweep.Followers();

            var tweet_tweep_pairs = tweets
                .Select(x =>
                    x.Status.Retweeted ?
                    new
                    {
                        tweet = new Tweet(x.Status.RetweetedStatus),
                        tweep = new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None),
                        weight = x.RetweetCount / (1.0 + new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None).Clout())
                    }
                    :
                    new
                    {
                        tweet = x,
                        tweep = x.Tweep(),
                        weight = x.RetweetCount / (1.0 + x.Tweep().Clout())
                    })
                .Where(x => x.tweep.Clout() > minClout)
                .Where(x => x.weight >= minWeight);

            if (tweet_tweep_pairs.Count() > 0)
            {
                RuntimeSettings.AverageWeight = RuntimeSettings.AverageWeight > 0.0 ?
                    (RuntimeSettings.AverageWeight + tweet_tweep_pairs.Average(x => x.weight)) / 2.0 : tweet_tweep_pairs.Average(x => x.weight);
            }
            else
            {
                RuntimeSettings.AverageWeight = RuntimeSettings.AverageWeight > 0.0 ?
                    (RuntimeSettings.AverageWeight) / 2.0 : 0.0;
            }
        }

        private void FindPotentialTweets(IEnumerable<Tweet> tweets)
        {
            var minWeight = GetMinWeight();
            var minRetweets = GetMinRetweets();
            var friendsAndFollows = PrimaryTweep.Followers().Select(x => x.ID);

            var tweet_tweep_pairs = tweets
                .Select(x =>
                    x.Status.Retweeted ?
                    new
                    {
                        tweet = new Tweet(x.Status.RetweetedStatus),
                        tweep = new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None),
                        weight = x.RetweetCount / (1.0 + new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None).Clout())
                    }
                    :
                    new
                    {
                        tweet = x,
                        tweep = x.Tweep(),
                        weight = x.RetweetCount / (1.0 + x.Tweep().Clout())
                    })
                    .Where(x => Encoding.UTF8.GetByteCount(x.tweet.TweetText) == x.tweet.TweetText.Length) //Only ASCII for me...
                    .Where(x => x.weight >= minWeight)
                    .Where(x=>x.tweet.RetweetCount >= minRetweets);

            if (tweet_tweep_pairs.Count() > 0)
            {
                RuntimeSettings.PotentialReTweets = tweet_tweep_pairs
                    .Where(x => friendsAndFollows.Contains(x.tweep.UniqueKey))
                    .Select(x => x.tweet)
                    .Union(RuntimeSettings.PotentialReTweets, Tweet.GetTweetTextComparer())
                    .OrderByDescending(x => x.RetweetCount)
                    .Take(POTENTIAL_TWEET_BUFFER_MAX)
                    .ToList();

                RuntimeSettings.PotentialTweets = tweet_tweep_pairs
                    .Where(x => !friendsAndFollows.Contains(x.tweep.UniqueKey) &&
                        //x.tweet.Status.Entities.UserMentions.Count() == 0 &&
                        (x.tweet.Status.Entities.UrlMentions.Count() > 0 || x.tweet.Status.Entities.MediaMentions.Count() > 0))
                    .Select(x => x.tweet)
                    .Union(RuntimeSettings.PotentialTweets, Tweet.GetTweetTextComparer())
                    .OrderByDescending(x => x.RetweetCount)
                    .Take(POTENTIAL_TWEET_BUFFER_MAX)
                    .ToList();
            }
        }

        private void FindTweepsToFollow(IEnumerable<Tweet> tweets)
        {
            var minClout = GetMinClout();
            var minWeight = GetMinWeight();
            var friendsAndFollows = PrimaryTweep.Followers().Select(x=>x.ID);
            var tweet_tweep_pairs = tweets
                .Select(x =>
                    x.Status.Retweeted ?
                    new
                    {
                        tweet = new Tweet(x.Status.RetweetedStatus),
                        tweep = new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None),
                        weight = x.RetweetCount / (1.0 + new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None).Clout())
                    }
                    :
                    new
                    {
                        tweet = x,
                        tweep = x.Tweep(),
                        weight = x.RetweetCount / (1.0 + x.Tweep().Clout())
                    })
                .Where(x => !friendsAndFollows.Contains(x.tweep.UniqueKey))
                .Where(x => x.tweep.User.LangResponse == PrimaryTweep.User.LangResponse)
                .Where(x => x.tweep.Clout() > minClout)
                .Where(x => x.weight >= minWeight);

            //Update Existing
            tweet_tweep_pairs
                    .Select(x => x.tweep)
                    .ToList()
                    .ForEach(x =>
                    {
                        for (int i = 0; i < RuntimeSettings.PotentialTweeps.Count(); i++)
                        {
                            if (RuntimeSettings.PotentialTweeps[i].Item2.Equals(x))
                            {
                                var tweep = RuntimeSettings.PotentialTweeps[i].Item2;
                                
                                if(tweep.Type != Tweep.TweepType.Target)
                                    tweep.Type = Tweep.TweepType.None;

                                RuntimeSettings.PotentialTweeps[i] = new Tuple<int, Tweep>(
                                    RuntimeSettings.PotentialTweeps[i].Item1 + 1,
                                    tweep);
                            }
                        }
                    });

            //Add New
            tweet_tweep_pairs
                    .Select(x => x.tweep)
                    .Except(RuntimeSettings.PotentialTweeps.Select(x => x.Item2))
                    .ToList()
                    .ForEach(x =>
                    {
                        RuntimeSettings.PotentialTweeps.Add(new Tuple<int, Tweep>(1, x));
                    });

            //Limit
            RuntimeSettings.PotentialTweeps = RuntimeSettings.PotentialTweeps
                .OrderByDescending(x => x.Item2.Clout())
                .Take(POTENTIAL_TWEEP_BUFFER_MAX)
                .ToList();
        }

        private int GetMinClout()
        {
            /*
            var friends = PrimaryTweep.Followers().Select(x=>x.Value)
                .Where(x => x.Type == Tweep.TweepType.Follower || x.Type == Tweep.TweepType.Mutual);
            double minClout = friends.Count() + 1.0;
            return (int)Math.Max(minClout, friends.Count() > 0 ? Math.Floor(friends.Average(x => x.Clout())) : 0);
            */
            return PrimaryTweep.Followers().Count();
        }

        private double GetMinWeight()
        {
            if (RuntimeSettings.AverageWeight < 0.00001) RuntimeSettings.AverageWeight = 0.0;
            return RuntimeSettings.AverageWeight;
        }

        private double GetMinRetweets()
        {
            if (RuntimeSettings.Tweeted != null && RuntimeSettings.Tweeted.Count > 0)
                return RuntimeSettings.Tweeted.Average(x => x.RetweetCount) * 0.65;
            else
                return 1.0;
        }

        private IEnumerable<Tweet> RespondToTweets(IEnumerable<Tweet> tweets)
        {

            var repliedTo = new List<Tweet>();
            if (Messages != null)
            {
                foreach (var t in tweets)
                {
                    string tweetedBy = t.User.Identifier.ScreenName.ToLower();
                    if (!NoTweetList.Any(x => x == tweetedBy) && //Don't bug people with constant retweets
                        !t.TweetText.ToLower().Contains(NoTweetList[0]) && //Don't bug them even if they are mentioned in the tweet
                        (!OnlyWithMentions || t.Status.Entities.UserMentions.Count > 0) //OPTIONAL: Only respond to tweets that mention someone
                        )
                    {
                        //Dont want to keep hitting the same person over and over so add them to the ignore list
                        NoTweetList.Add(tweetedBy);
                        //If they were mentioned in a tweet they get ignored in the future just in case they reply
                        NoTweetList.AddRange(t.Status.Entities.UserMentions.Where(um => !string.IsNullOrEmpty(um.ScreenName)).Select(um => um.ScreenName));

                        string message = "";
                        if (t.User.FollowersCount > 9999)
                            //TODO: It would be very cool to have the code branch here for custom tweets to popular twitter accounts
                            //IDEA: Maybe have it text you for a response
                            message = Messages.OrderBy(x => Guid.NewGuid()).FirstOrDefault();
                        else
                            //Randomly select response from list of possible responses
                            message = Messages.OrderBy(x => Guid.NewGuid()).FirstOrDefault();

                        //Tweet it
                        try
                        {
                            TwitterModel.Instance.UpdateStatus(message + " RT @" + t.User.Identifier.ScreenName + " " + t.TweetText, processStatus: false);
                        }
                        catch (Exception ex) { log.WriteLine("{0}: TweetBot Error: {1}", DateTime.Now, ex.ToString()); }

                        repliedTo.Add(t);

                        //Wait at least 1 minute between tweets so it doesnt look bot-ish with fast retweets
                        //Add some extra random timing somewhere between 0-2 minutes
                        //The shortest wait will be 1 minute the longest will be 3
                        int randomTime = 60000 + (1000 * Enumerable.Range(0, 120).OrderBy(x => Guid.NewGuid()).FirstOrDefault());
                        System.Threading.Thread.Sleep(randomTime);
                    }
                }
            }
            return repliedTo;
        }
    }

    #region TweetBotSettings
    public class TweetBotSettings : ConfigurationSection
    {
        private static TweetBotSettings settings = ConfigurationManager.GetSection("TweetBotSettings") as TweetBotSettings;

        public static TweetBotSettings Get { get { return settings; } }

        [ConfigurationProperty("Messages", IsKey = false, IsRequired = true)]
        public MessageCollection Messages { get { return (MessageCollection)base["Messages"]; } }

        [ConfigurationProperty("Filters", IsKey = false, IsRequired = false)]
        public FilterCollection Filters { get { return (FilterCollection)base["Filters"]; } }

        [ConfigurationProperty("Settings", IsKey = false, IsRequired = false)]
        public SettingCollection Settings { get { return (SettingCollection)base["Settings"]; } }
    }

    public class SettingCollection : ConfigurationElementCollection
    {
        protected override ConfigurationElement CreateNewElement()
        {
            return new Setting();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((Setting)element).Key;
        }

        public Setting this[int idx]
        {
            get
            {
                return (Setting)BaseGet(idx);
            }
        }

        public Setting this[string key]
        {
            get
            {
                return (Setting)BaseGet(key);
            }
        }
    }

    public class FilterCollection : ConfigurationElementCollection
    {
        protected override ConfigurationElement CreateNewElement()
        {
            return new Filter();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((Filter)element).Key;
        }

        public Filter this[int idx]
        {
            get
            {
                return (Filter)BaseGet(idx);
            }
        }

        public Filter this[string key]
        {
            get
            {
                return (Filter)BaseGet(key);
            }
        }
    }

    public class MessageCollection : ConfigurationElementCollection
    {
        protected override ConfigurationElement CreateNewElement()
        {
            return new Message();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((Message)element).Key;
        }

        public Message this[int idx]
        {
            get
            {
                return (Message)BaseGet(idx);
            }
        }
    }

    public class Setting : ConfigurationElement
    {
        [ConfigurationProperty("key", IsKey = false, IsRequired = true)]
        public string Key { get { return (string)base["key"]; } set { base["key"] = value; } }
        [ConfigurationProperty("value", IsKey = false, IsRequired = true)]
        public bool Value { get { return (bool)base["value"]; } set { base["value"] = value; } }
    }

    public class Filter : ConfigurationElement
    {
        [ConfigurationProperty("key", IsKey = false, IsRequired = true)]
        public string Key { get { return (string)base["key"]; } set { base["key"] = value; } }
        [ConfigurationProperty("value", IsKey = false, IsRequired = true)]
        public bool Value { get { return (bool)base["value"]; } set { base["value"] = value; } }
    }

    public class Message : ConfigurationElement
    {
        [ConfigurationProperty("key", IsKey = true, IsRequired = true)]
        public string Key { get { return (string)base["key"]; } set { base["key"] = value; } }
        [ConfigurationProperty("value", IsKey = false, IsRequired = true)]
        public string Value { get { return (string)base["value"]; } set { base["value"] = value; } }
    }

    public class TweetBotRuntimeSettings : RepositoryEntity
    {
        public Guid SettingsGuid { get; set; }

        public double AverageWeight { get; set; }
        public DateTime LastTweetTime { get; set; }
        public bool TweetOrRetweet { get; set; }
        public int TweetsSentSinceLastFriendRequest { get; set; }
        public List<Tweet> PotentialTweets { get; set; }
        public List<Tweet> PotentialReTweets { get; set; }
        public List<Tweet> Tweeted { get; set; }
        public List<Tuple<int, Tweep>> PotentialTweeps { get; set; }

        public TweetBotRuntimeSettings()
        {
            SettingsGuid = Guid.NewGuid();
            PotentialTweets = new List<Tweet>();
            PotentialReTweets = new List<Tweet>();
            Tweeted = new List<Tweet>();
            PotentialTweeps = new List<Tuple<int, Tweep>>();
            LastTweetTime = DateTime.MaxValue;
        }

        public override string UniqueKey
        {
            get { return SettingsGuid.ToString(); }
        }

        public override bool IsEqual(RepositoryEntity other)
        {
            return other.UniqueKey == this.UniqueKey;
        }
    }

    #endregion
}

