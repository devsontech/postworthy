﻿using Postworthy.Models.Repository;
using Postworthy.Models.Twitter;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Postworthy.Models.Streaming
{
    public class PassThroughProcessingStep : IProcessingStep, ITweepProcessingStep
    {
        protected TextWriter log;
        protected string screenName;

        public void Init(string screenname, System.IO.TextWriter log)
        {
            screenName = screenname;
            this.log = log;
        }

        public Task<IEnumerable<Tweet>> ProcessItems(IEnumerable<Tweet> tweets)
        {
            return Task<IEnumerable<Tweet>>.Factory.StartNew(new Func<IEnumerable<Tweet>>(() =>
                {
                    return tweets;
                }));
        }

        protected virtual void StoreInRepository(IEnumerable<Tweep> tweeps)
        {
            CachedRepository<Tweep>.Instance(screenName).Save(screenName + TwitterModel.Instance(screenName).FRIENDS, tweeps);
            log.WriteLine("{0}: {1} Freinds Added ({3}) for {2}", DateTime.Now, tweeps.Count(), screenName, string.Join(",", tweeps.Select(x => x.ScreenName)));
        }

        protected void RemoveOldTweeps()
        {
            CachedRepository<Tweep>.Instance(screenName).Delete(screenName + TwitterModel.Instance(screenName).FRIENDS);
            log.WriteLine("{0}: Removed Old Friends for {1}", DateTime.Now, screenName);
        }

        public void Shutdown()
        {

        }

        public Task<IEnumerable<Core.LazyLoader<Tweep>>> ProcessTweeps(IEnumerable<Core.LazyLoader<Tweep>> tweeps)
        {
            return Task<IEnumerable<Core.LazyLoader<Tweep>>>.Factory.StartNew(new Func<IEnumerable<Core.LazyLoader<Tweep>>>(() =>
            {
                RemoveOldTweeps();
                StoreInRepository(tweeps.Select(x => x.Value).ToList());
                return tweeps;
            }));
        }
    }
}
