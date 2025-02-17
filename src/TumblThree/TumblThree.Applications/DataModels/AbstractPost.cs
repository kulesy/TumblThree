﻿namespace TumblThree.Applications.DataModels
{
    public enum PostType
    {
        Binary,
        Text
    }

    public abstract class AbstractPost
    {
        public PostType PostType { get; protected set; }

        public string Url { get; }

        public string Id { get; }

        public string Date { get; }

        public string Filename { get; }

        public string DbType { get; protected set; }

        public string TextFileLocation { get; protected set; }

        protected AbstractPost(string url, string id, string date, string filename)
        {
            Url = url;
            Id = id;
            Date = date;
            Filename = filename;
        }
    }
}
